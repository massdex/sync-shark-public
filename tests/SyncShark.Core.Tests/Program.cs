using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
#pragma warning disable CS8321

if (args is ["--v2-local-constituent-checks", var constituentRoot, var constituent])
{
    await Massdex.SyncShark.V2.LocalChecks.RunConstituentAsync(constituentRoot, constituent);
    return 0;
}

if (args is ["--blocker-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.BlockerChecksAsync();
    return 0;
}

if (args is ["--first-sync-completion-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.FirstSyncCompletionChecksAsync();
    return 0;
}

if (args is ["--upload-payload-checks"])
{
    await UploadPayloadChecks.RunAsync();
    return 0;
}
if (args is ["--upload-payload-regression"])
{
    await Massdex.SyncShark.V2.ExecutorChecks.RunPayloadRegressionAsync();
    return 0;
}

if (args is ["--resilience-checks"])
{
    await ResilienceChecks.RunAsync();
    await ReadStallChecks.RunAsync();
    await RootVerifyTimeoutChecks.RunAsync();
    await DropboxDownloadRetryChecks.RunAsync();
    await Massdex.SyncShark.V2.RunChecks.HashCheckpointsAsync();
    await Massdex.SyncShark.V2.RunChecks.HashCheckpointBatchAsync();
    return 0;
}

if (args is ["--v2-download-kill-checks", var downloadKillRoot])
{
    await Massdex.SyncShark.V2.DirectDownloadInterruptionChecks.RunAsync(Path.GetFullPath(downloadKillRoot));
    return 0;
}
if (args is ["--v2-download-kill-child", var downloadKillChildRoot, var downloadKillPhase])
{
    await Massdex.SyncShark.V2.DirectDownloadInterruptionChecks.CrashAsync(Path.GetFullPath(downloadKillChildRoot), downloadKillPhase);
    return 0;
}

if (args is ["--v2-recovery-kill-checks", var recoveryKillRoot])
{
    await Massdex.SyncShark.V2.LocalRecoveryInterruptionChecks.RunAsync(Path.GetFullPath(recoveryKillRoot));
    return 0;
}
if (args is ["--v2-recovery-kill-child", var recoveryKillChildRoot, var recoveryKillPhase])
{
    await Massdex.SyncShark.V2.LocalRecoveryInterruptionChecks.CrashAsync(Path.GetFullPath(recoveryKillChildRoot), recoveryKillPhase);
    return 0;
}

var inheritedTestProductRoot = args switch
{
    ["--hard-link-probe", _, _, var root] => Path.GetFullPath(root),
    ["--acl-crash-probe", _, _, var root] => Path.GetFullPath(root),
    ["--cleanup-crash-probe", _, _, _, _, var root] => Path.GetFullPath(root),
    ["--true-sync-crash-probe", _, _, var root] => Path.GetFullPath(root),
    ["--cloud-pro-wal-crash-probe", _, var root] => Path.GetFullPath(root),
    ["--authority-fresh-create-crash-probe", _, _, var root] => Path.GetFullPath(root),
    ["--control-allocation-crash-probe", _, _, _, var root] => Path.GetFullPath(root),
    ["--recovery-spool-crash-probe", _, var root] => Path.GetFullPath(root),
    ["--cross-pair-generation-crash-probe", _, _, var root] => Path.GetFullPath(root),
    ["--cross-pair-directory-effect-crash-probe", _, _, var root] => Path.GetFullPath(root),
    ["--cli-bridge-client-probe", var root, _] => Path.GetFullPath(root),
    ["--native-manual-restore-interruption-crash", var root, _] => Path.GetFullPath(root + "-product-runtime"),
    _ => null
};
using var isolatedProductRuntime = new IsolatedTestProductRuntime(inheritedTestProductRoot);

if (args is ["--v2-million-memory-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.MillionItemPrepareMemoryAsync();
    return 0;
}

if (args is ["--essential-successor-checks", var successorRoot])
{
    var root=Path.GetFullPath(successorRoot);
    await EssentialSuccessorChecks.RunAsync(Path.Combine(root,"configuration"));
    await EssentialSuccessorChecks.RunObservationAsync(Path.Combine(root,"observations"));
    await EssentialSuccessorChecks.RunLegacyWalAsync(Path.Combine(root,"legacy-wal"));
    await CompletedCopyMoveChecks.RunAsync(Path.Combine(root,"legacy-copy"),legacySuccessorOnly:true);
    await EssentialSuccessorChecks.RunMonitorAsync(Path.Combine(root,"monitor"));
    await EssentialSuccessorChecks.RunMigrationAsync(Path.Combine(root,"migration"));
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"journal"));
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"reviewed-lineage"),includeLegacyLineage:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"current-journal"),retainOlderCuts:false);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"dispatch"),dispatchOnly:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"ownership"),includeOwnership:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"pending-safety"),includeSafety:true);
    await EssentialSuccessorChecks.RunHistoryDispositionAsync(Path.Combine(root,"history-disposition"));
    await EssentialSuccessorChecks.RunRewindAsync(Path.Combine(root,"rewind"));
    await EssentialSuccessorChecks.RunExplorerAsync(Path.Combine(root,"explorer"));
    await NormalizedOriginalConsentChecks.RunBothAbsentCollectionAsync(Path.Combine(root,"both-absence"),successorOnly:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"decisions"),includeDecisions:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"resolutions"),includeResolution:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"rewind-journal"),includeRewind:true);
    await SettledWorkLifetimeChecks.RunRegistrationAsync(Path.Combine(root,"group"),successorGroupOnly:true);
    await SettledWorkLifetimeChecks.RunRegistrationAsync(Path.Combine(root,"committed-outcome"),successorCompletionOnly:true);
    await SettledWorkLifetimeChecks.RunRegistrationAsync(Path.Combine(root,"closed-prerequisite"),successorClosureOnly:true);
    await ConsentLifetimeChecks.RunAsync(Path.Combine(root,"consent"),successorOnly:true);
    await EssentialSuccessorChecks.RunJournalAsync(Path.Combine(root,"journal-points"),includePoints:true);
    await BoundedPresentationHistoryChecks.RunAsync(Path.Combine(root,"recent-history"),successorOnly:true);
    await SettledWorkLifetimeChecks.RunRegistrationAsync(Path.Combine(root,"lifecycle-history"),successorHistoryOnly:true);
    await CompletedCopyMoveChecks.RunAsync(Path.Combine(root,"copy"),successorOnly:true);
    return 0;
}

if (args is ["--settled-action-registration-checks", var settledRegistrationRoot])
{
    await SettledWorkLifetimeChecks.RunRegistrationAsync(Path.GetFullPath(settledRegistrationRoot));
    return 0;
}

if (args is ["--cold-command-admission-checks", var coldCommandRoot])
{
    await ColdCommandAdmissionChecks.RunAsync(Path.GetFullPath(coldCommandRoot));
    return 0;
}

if (args is ["--initial-planner-progress-checks", var initialPlannerProgressRoot])
{
    await CloudPairComparisonChecks.RunInitialProgressAsync(Path.GetFullPath(initialPlannerProgressRoot));
    return 0;
}

if (args is ["--blocked-inventory-checks", var blockedInventoryRoot])
{
    await BlockedInventoryChecks.RunAsync(Path.GetFullPath(blockedInventoryRoot));
    return 0;
}

if (args is ["--release-recovery-admission-checks", var releaseRecoveryRoot])
{
    ReleaseRecoveryAdmissionChecks.Run(Path.GetFullPath(releaseRecoveryRoot));
    return 0;
}

if (args is ["--authority-wal-checks", var walPolicyRoot])
{
    await AuthorityWalChecks.RunAsync(Path.GetFullPath(walPolicyRoot));
    return 0;
}

if (args is ["--cli-bridge-delivery-checks", var bridgeDeliveryRoot])
{
    await CliChecks.RunNativeBridgeChecksAsync(Path.GetFullPath(bridgeDeliveryRoot));
    return 0;
}
if (args is ["--cli-bridge-client-probe", _, var bridgeDeliveryCase])
    return CliChecks.RunNativeBridgeClientProbe(bridgeDeliveryCase);
if (args is ["--observation-progress-admission-checks", var observationAdmissionRoot])
{
    await ActivityPersistenceConcurrencyChecks.RunObservationAdmissionAsync(Path.GetFullPath(observationAdmissionRoot));
    return 0;
}

// Bounded diagnostic entry point for the exact production read-only health
// path. No initialization, migration, provider call or database copy occurs.
if (args is ["--readonly-health-check", var healthDatabasePath])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var elapsed = Stopwatch.StartNew();
    try
    {
        await new SyncAuthorityDatabase(Path.GetFullPath(healthDatabasePath)).VerifyHealthAsync(deadline.Token,
            message => Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}"));
        Console.WriteLine($"PASS read-only health check completed in {elapsed.Elapsed.TotalSeconds:F3}s");
        return 0;
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        Console.WriteLine($"INCOMPLETE read-only health check cancelled after {elapsed.Elapsed.TotalSeconds:F3}s; no sync or database write was attempted");
        return 130;
    }
}

if (args is ["--cli-deadline-checks"])
{
    await CliDeadlineChecks.RunAsync();
    return 0;
}

if (args is ["--preserve-current-recovery-checkpoint", var checkpointSource, var checkpointRoot, var checkpointSettings])
{
    await CurrentStateRecoveryCheckpoint.RunAsync(checkpointSource, checkpointRoot, checkpointSettings);
    return 0;
}

if (args is ["--resume-current-recovery-checkpoint", var resumeSource, var resumeRoot, var resumeSettings])
{
    await CurrentStateRecoveryCheckpoint.RunAsync(resumeSource, resumeRoot, resumeSettings, resume: true);
    return 0;
}

if (args is ["--current-state-readback-checks", var readbackRoot])
{
    await RecoveryReadbackChecks.RunAsync(Path.GetFullPath(readbackRoot));
    return 0;
}

if (args is ["--observation-identity-progress-checks", var identityRecoveryRoot])
{
    await ObservationIdentityProgressChecks.RunAsync(Path.GetFullPath(identityRecoveryRoot));
    return 0;
}

// Exercise the actual candidate read against retained state without opening a
// runtime, initializing/migrating the database or issuing a provider/file effect.
if (args is ["--readonly-copy-move-candidates", var moveDatabasePath, var movePairId, var moveDeadlineText])
{
    var seconds = double.Parse(moveDeadlineText, System.Globalization.CultureInfo.InvariantCulture);
    if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 30)
        throw new ArgumentOutOfRangeException(nameof(moveDeadlineText));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var watch = Stopwatch.StartNew();
    try
    {
        var database = new SyncAuthorityDatabase(Path.GetFullPath(moveDatabasePath));
        var moves = await database.LoadCompletedCopyMovesAsync(movePairId, deadline.Token);
        var enumerationSeconds = watch.Elapsed.TotalSeconds;
        var validations = new List<double>();
        foreach (var move in moves)
        {
            var validation = Stopwatch.StartNew();
            if (!await database.CompletedCopyMoveMatchesAsync(movePairId, move, deadline.Token))
                throw new InvalidOperationException("The current exact candidate did not pass its production guard.");
            validations.Add(validation.Elapsed.TotalSeconds);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, candidates = moves.Count,
            seconds = watch.Elapsed.TotalSeconds, enumerationSeconds, validationSeconds = validations, moves }));
        return 0;
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, cancelled = true,
            seconds = watch.Elapsed.TotalSeconds, effectsAttempted = 0 }));
        return 130;
    }
}

if (args is ["--aggregate-state-recovery-checks"])
{
    await DualSlotConcurrencyChecks.RunAsync();
    await DualSlotRecovery();
    await AggregateMigrationRecovery();
    await AggregateCorruptStateRefusal();
    Console.WriteLine("PASS U02 aggregate state: missing slots migrate; valid slots recover; corrupt-only slots preserve bytes and refuse recreation");
    return 0;
}

if (args is ["--cli-local-path-checks"])
{
    await CliChecks.LocalPathArgumentChecksAsync();
    return 0;
}

if (args is ["--changed-only-hashing-checks"])
{
    await ChangedOnlyHashing();
    Console.WriteLine("PASS complete safety observations hash held bytes; only continuous change tracking reuses unchanged entries");
    return 0;
}

if (args is ["--local-hash-freshness-checks", var localHashFreshnessRoot])
{
    await LocalHashFreshnessChecks.RunAsync(Path.GetFullPath(localHashFreshnessRoot));
    return 0;
}

if (args is ["--local-scan-allocation-checks", var localScanAllocationRoot])
{
    await PilotLocalScannerAllocationChecks.RunAsync(Path.GetFullPath(localScanAllocationRoot));
    return 0;
}

// Existing user-authorized diagnostic scope only: the actual production scanner reads
// held bytes under its normal path checks. No pair state, inventory, or files are written.
if (args is ["--readonly-local-scan-timing", var localScanTimingRoot])
    return await PilotLocalScannerAllocationChecks.MeasureReadOnlyAsync(Path.GetFullPath(localScanTimingRoot));
if (args is ["--readonly-local-concurrent-scan-timing", var localConcurrentScanTimingRoot])
    return await PilotLocalScannerAllocationChecks.MeasureReadOnlyAsync(Path.GetFullPath(localConcurrentScanTimingRoot), concurrent: true);
if (args is ["--local-concurrent-scan-checks", var localConcurrentScanRoot])
{
    await PilotLocalScannerAllocationChecks.ConcurrentAsync(Path.GetFullPath(localConcurrentScanRoot));
    return 0;
}

if (args is ["--adaptive-resource-checks", var adaptiveRoot])
{
    await AdaptiveResourceChecks.RunAsync(Path.GetFullPath(adaptiveRoot));
    return 0;
}
if (args is ["--adaptive-resource-cli-checks", var adaptiveCliRoot])
{
    await ConcurrentTransferChecks.RunCliAsync(Path.GetFullPath(adaptiveCliRoot), 64, 64, adaptive: true);
    return 0;
}
if (args is ["--performance-resource-cli-checks", var performanceCliRoot])
{
    await ConcurrentTransferChecks.RunCliAsync(Path.GetFullPath(performanceCliRoot), 64, 256,
        adaptive: true, resourceMode: "Performance");
    return 0;
}
// Opt-in first-sync apply throughput: production CLI and planner/runner; uploads join a simulated
// shared batch commit of the given latency (Dropbox finish_batch_v2 is ~1-2 s). A fixed 64-worker
// limit stands in for Auto's worker cap, without the machine-pressure backoff making the measurement
// depend on this computer's free memory.
if (args is ["--apply-window-benchmark", var applyWindowRoot, var applyWindowFiles, var applyWindowCommitMs])
{
    await ConcurrentTransferChecks.RunCliAsync(Path.GetFullPath(applyWindowRoot), int.Parse(applyWindowFiles), 64,
        batchCommitLatency: TimeSpan.FromMilliseconds(int.Parse(applyWindowCommitMs)));
    return 0;
}

if (args is ["--connection-reuse-checks", var connectionReuseRoot])
{
    await ConcurrentTransferChecks.RunConnectionReuseChecksAsync(Path.GetFullPath(connectionReuseRoot));
    return 0;
}

if (args is ["--provider-account-scope-checks", var accountScopeRoot])
{
    await ProviderAccountScopeChecks.RunAsync(Path.GetFullPath(accountScopeRoot));
    return 0;
}

if (args is ["--oauth-presentation-checks", var oauthPresentationRoot])
{
    await OAuthCallbackValidation();
    await OAuthAcknowledgement();
    await OAuthPresentationChecks.RunAsync(Path.GetFullPath(oauthPresentationRoot));
    return 0;
}

if (args is ["--storage-inventory-checks", var storageInventoryRoot])
{
    await StorageInventoryChecks.RunAsync(Path.GetFullPath(storageInventoryRoot), CreateDirectoryJunction);
    return 0;
}

if (args is ["--lease-renewal-checks", var leaseRenewalRoot])
{
    await LeaseRenewalChecks.RunAsync(Path.GetFullPath(leaseRenewalRoot));
    return 0;
}

if (args is ["--monitor-failure-correction-checks", var monitorFailureRoot])
{
    await MonitorFailureCorrectionChecks.RunAsync(Path.GetFullPath(monitorFailureRoot));
    return 0;
}

if (args is ["--cross-pair-window-continuation-checks",var crossWindowRoot])
{
    await NormalizedOriginalConsentChecks.RunCrossPairWindowContinuationAsync(Path.GetFullPath(crossWindowRoot));
    return 0;
}

if (args is ["--both-absent-dependency-checks",var bothAbsentDependencyRoot])
{
    await NormalizedOriginalConsentChecks.RunBothAbsentPlateauAsync(Path.GetFullPath(bothAbsentDependencyRoot),verifyDependencyHold:true);
    return 0;
}

if (args is ["--both-absent-collection-checks",var bothAbsentCollectionRoot])
{
    await NormalizedOriginalConsentChecks.RunBothAbsentCollectionAsync(Path.GetFullPath(bothAbsentCollectionRoot));
    return 0;
}

if (args is ["--both-absent-plateau-checks",var bothAbsentRoot])
{
    await NormalizedOriginalConsentChecks.RunBothAbsentPlateauAsync(Path.GetFullPath(bothAbsentRoot));
    return 0;
}

if (args is ["--monitor-lifetime-budget-checks",var monitorBudgetRoot])
{
    await MonitorLifetimeChecks.RunBudgetAsync(Path.GetFullPath(monitorBudgetRoot));
    return 0;
}

if (args is ["--native-monitor-callback-checks", var nativeMonitorRoot])
{
    await NativeMonitorCallbackChecks.RunAsync(Path.GetFullPath(nativeMonitorRoot));
    return 0;
}

if (args is ["--native-cloud-registration-prerequisite", var nativeRegistrationRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeCloudEditChecks.RunRegistrationPrerequisiteAsync(Path.GetFullPath(nativeRegistrationRoot));
    return 0;
}

if (args is ["--native-cloud-edit-checks", var nativeCloudEditRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeCloudEditChecks.RunAsync(Path.GetFullPath(nativeCloudEditRoot), CreateDirectoryJunction);
    return 0;
}

if (args is ["--native-cloud-edit-checks", var nativeCloudPartialRoot, "--partial-only"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeCloudEditChecks.RunAsync(Path.GetFullPath(nativeCloudPartialRoot), CreateDirectoryJunction,
        partialOnly: true);
    return 0;
}

if (args is ["--authority-history-admission-checks", var authorityAdmissionRoot])
{
    throw new InvalidOperationException("M7 history-expiration source and controlled proof are held for mandatory independent review; this release does not compile those entry points.");
}

if (args is ["--authority-history-retention-checks", var authorityHistoryRoot])
{
    throw new InvalidOperationException("M7 history-expiration source and controlled proof are held for mandatory independent review; this release does not compile those entry points.");
}

if (args is ["--native-local-authority-checks", var nativeLocalRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeLocalAuthorityChecks.RunAsync(Path.GetFullPath(nativeLocalRoot));
    return 0;
}

if (args is ["--native-authority-interruption-checks", var nativeAuthorityRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeAuthorityInterruptionChecks.RunAsync(Path.GetFullPath(nativeAuthorityRoot));
    return 0;
}

if (args is ["--native-authority-interruption-crash", var nativeAuthorityCrashRoot, var nativeAuthorityCrashMode])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeAuthorityInterruptionChecks.CrashAsync(Path.GetFullPath(nativeAuthorityCrashRoot), nativeAuthorityCrashMode);
    return 0;
}

if (args is ["--native-manual-restore-interruption-checks", var nativeManualRestoreRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeManualRestoreInterruptionChecks.RunAsync(Path.GetFullPath(nativeManualRestoreRoot));
    return 0;
}

if (args is ["--native-manual-restore-interruption-crash", var nativeManualRestoreCrashRoot, var nativeManualRestoreCrashPhase])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeManualRestoreInterruptionChecks.CrashAsync(Path.GetFullPath(nativeManualRestoreCrashRoot), nativeManualRestoreCrashPhase);
    return 0;
}

if (args is ["--native-local-authority-crash", var nativeCrashRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await NativeLocalAuthorityChecks.CrashLegacyCreateAsync(Path.GetFullPath(nativeCrashRoot));
    return 0;
}

if (args is ["--activity-event-checks", var activityEventRoot])
{
    await ActivityEventChecks.RunAsync(Path.GetFullPath(activityEventRoot));
    return 0;
}

if (args is ["--activity-persistence-concurrency-checks", var activityPersistenceRoot])
{
    await ActivityPersistenceConcurrencyChecks.RunAsync(Path.GetFullPath(activityPersistenceRoot));
    return 0;
}

if (args is ["--inventory-log-batching-checks", var inventoryLogRoot])
{
    await ActivityPersistenceConcurrencyChecks.RunInventoryBatchingAsync(Path.GetFullPath(inventoryLogRoot));
    return 0;
}

if (args is ["--durable-activity-context-checks", var durableActivityContextRoot])
{
    await DurableActivityContextChecks.RunAsync(Path.GetFullPath(durableActivityContextRoot));
    return 0;
}

if (args is ["--activity-context-storage-checks", var activityContextStorageRoot])
{
    await ActivityContextStorageChecks.RunAsync(Path.GetFullPath(activityContextStorageRoot));
    return 0;
}

if (args is ["--retained-activity-scaling-checks", var retainedActivitySource, var retainedActivityRoot])
{
    await RetainedActivityScalingChecks.RunAsync(Path.GetFullPath(retainedActivitySource), Path.GetFullPath(retainedActivityRoot));
    return 0;
}

if (args is ["--retained-activity-read-checks", var retainedReadSource, var retainedReadRun])
{
    await RetainedActivityScalingChecks.ReadCurrentAsync(Path.GetFullPath(retainedReadSource), retainedReadRun);
    return 0;
}

if (args is ["--retained-activity-pages-only-checks", var retainedPagesSource, var retainedPagesRun])
{
    await RetainedActivityScalingChecks.ReadCurrentAsync(Path.GetFullPath(retainedPagesSource), retainedPagesRun,
        verifyHealth: false);
    return 0;
}

if (args is ["--retained-planning-read-profile"])
{
    await RetainedRetryPreviewChecks.ReadPlanningProfileAsync();
    return 0;
}

if (args is ["--retained-planning-profile", var planningSource, var planningRoot, var planningRun, var planningPair])
{
    await RetainedRetryPreviewChecks.RunPlanningProfileAsync(Path.GetFullPath(planningSource), Path.GetFullPath(planningRoot),
        planningRun, planningPair);
    return 0;
}

if (args is ["--retained-retry-preview-checks", var retrySource, var retryRoot, var retryRun, var retryPair])
{
    await RetainedRetryPreviewChecks.RunAsync(Path.GetFullPath(retrySource), Path.GetFullPath(retryRoot),
        retryRun, retryPair);
    return 0;
}

if (args is ["--retained-bridge-repair-checks", var retainedBridgeSource, var retainedBridgeRoot])
{
    await RetainedBridgeRepairChecks.RunAsync(Path.GetFullPath(retainedBridgeSource), Path.GetFullPath(retainedBridgeRoot));
    return 0;
}

if (args is ["--volume-identity-reuse-checks", var volumeIdentityRoot])
{
    await VolumeIdentityReuseChecks.RunAsync(Path.GetFullPath(volumeIdentityRoot));
    return 0;
}

if (args is ["--excluded-nested-pair-partition-checks", var nestedPartitionRoot])
{
    await ExcludedNestedPairPartitionChecks.RunAsync(Path.GetFullPath(nestedPartitionRoot));
    return 0;
}

if (args is ["--excluded-nested-cloud-typed-checks", var nestedCloudRoot])
{
    await ExcludedNestedCloudTypedAdmissionChecks.RunAsync(Path.GetFullPath(nestedCloudRoot));
    return 0;
}

if (args is ["--excluded-nested-pair-retry-checks", var nestedRetryRoot])
{
    await ExcludedNestedPairRetryChecks.RunAsync(Path.GetFullPath(nestedRetryRoot));
    return 0;
}

if (args is ["--corresponding-included-overlap-checks", var correspondingOverlapRoot])
{
    await CorrespondingIncludedOverlapChecks.RunAsync(Path.GetFullPath(correspondingOverlapRoot));
    return 0;
}

if (args is ["--conflict-content-endpoint-checks", var conflictContentRoot])
{
    await ConflictContentEndpointChecks.RunAsync(Path.GetFullPath(conflictContentRoot));
    return 0;
}

if (args is ["--google-drive-endpoint-navigation-checks", var driveNavigationRoot])
{
    await GoogleDriveEndpointNavigationChecks.RunAsync(Path.GetFullPath(driveNavigationRoot));
    return 0;
}

if (args is ["--residual-safety-checks"])
{
    await CloudProAuthorityPreflight.RunResidualSafetyDeltaAsync();
    return 0;
}

if (args is ["--discovery-activation-checks"])
{
    await DiscoveryActivationChecks.RunAsync();
    return 0;
}

if (args is ["--discovery-native-response-checks", var nativeResponseDirectory])
{
    await DiscoveryProtocolChecks.ReadNativeAsync(Path.GetFullPath(nativeResponseDirectory));
    return 0;
}

if (args is ["--discovery-protocol-checks"])
{
    await DiscoveryProtocolChecks.RunAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "--explorer-status-checks")
{
    await ExplorerStatusChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 1 && args[0] == "--native-status-root-checks")
{
    await ExplorerStatusChecks.NativeRootAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "--sharing-checks")
{
    await SharingChecks.RunAsync();
    return 0;
}

if (args is ["--google-revision-checks", var googleRevisionRoot])
{
    await GoogleDriveRevisionChecks.RunAsync(Path.GetFullPath(googleRevisionRoot));
    return 0;
}

if (args.Length == 2 && args[0] == "--google-drive-checks")
{
    await GoogleDriveChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args is ["--google-drive-recovery-checks"])
{
    await GoogleDriveRecoveryChecks.RunAsync();
    return 0;
}

if (args is ["--google-drive-live-checks", var googleLiveProfile, var googleLiveConnection,
    var googleLiveFolder, "--allow-test-writes"])
{
    try { await GoogleDriveLiveChecks.RunAsync(googleLiveProfile, googleLiveConnection, googleLiveFolder); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-release41-readback-guards"])
{
    GoogleDriveLiveChecks.Release41ReadbackGuardChecks();
    return 0;
}

if (args is ["--google-drive-release41-readback", var readbackProfile, var readbackConnection, var readbackFolder, var readbackName])
{
    try { await GoogleDriveLiveChecks.ReadRelease41Async(readbackProfile, readbackConnection, readbackFolder, readbackName); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-live-inspect", var inspectProfile, var inspectConnection, var inspectFolder])
{
    try { await GoogleDriveLiveChecks.RunAsync(inspectProfile, inspectConnection, inspectFolder, inspectOnly: true); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-live-metadata", var metadataProfile, var metadataConnection,
    var metadataFolder, "--allow-test-writes"])
{
    try { await GoogleDriveLiveChecks.RunAsync(metadataProfile, metadataConnection, metadataFolder, metadataOnly: true); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-live-sharing-inventory", var sharingInventoryProfile, var sharingInventoryConnection])
{
    try { await GoogleDriveLiveChecks.ReadSharingInventoryAsync(sharingInventoryProfile, sharingInventoryConnection); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-live-sharing-inspect", var sharingInspectProfile, var sharingInspectConnection,
    var sharingInspectFolder])
{
    try { await GoogleDriveLiveChecks.RunAsync(sharingInspectProfile, sharingInspectConnection, sharingInspectFolder, inspectSharing: true); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args is ["--google-drive-live-checks", var resumeProfile, var resumeConnection,
    var resumeFolder, "--resume-known-fixture", "--allow-test-writes"])
{
    try { await GoogleDriveLiveChecks.RunAsync(resumeProfile, resumeConnection, resumeFolder, resumeKnownFixture: true); return 0; }
    catch (Exception error) { GoogleDriveLiveChecks.ReportFailure(error); return 1; }
}

if (args.Length == 2 && args[0] == "--update-discovery-checks")
{
    UpdateDiscoveryChecks.Run(Path.GetFullPath(args[1]), CreateDirectoryJunction);
    return 0;
}

if (args is ["--public-update-checks"] or ["--public-update-live-read"])
{
    await PublicUpdateChecks.RunAsync(args[0] == "--public-update-live-read");
    return 0;
}

if (args.Length == 1 && args[0] == "--account-sharing-checks")
{
    await AccountSharingChecks.RunAsync();
    return 0;
}

if (args.Length == 1 && args[0] == "--mft-catalog-checks")
{
    try { MftCatalogChecks.Run(); return 0; }
    catch (Exception error) { Console.Error.WriteLine("MFT catalogue checks failed: " + error.Message); return 1; }
}

if (args.Length == 2 && args[0] == "--incremental-inventory-checks")
{
    await IncrementalInventoryChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--discovery-batch-checks")
{
    await DiscoveryBatchChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--account-routing-checks")
{
    await AccountRoutingChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--ignore-rule-checks")
{
    await IgnoreRuleChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--pair-policy-checks")
{
    await PairPolicyChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--file-storage-checks")
{
    await FileStorageChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2 && args[0] == "--workspace-iteration-checks")
{
    await WorkspaceIterationChecks.RunAsync(Path.GetFullPath(args[1]));
    return 0;
}

if (args is ["--v2-contract-checks"])
{
    await Massdex.SyncShark.V2.ContractsSmokeChecks.RunAsync();
    return 0;
}

if (args is ["--v2-storage-checks", var v2StorageRoot])
{
    await Massdex.SyncShark.V2.StorageChecks.RunAsync(Path.GetFullPath(v2StorageRoot));
    return 0;
}

if (args is ["--v2-storage-unicode-checks"])
{
    Massdex.SyncShark.V2.StorageChecks.RunUnicode();
    return 0;
}

if (args is ["--v2-storage-throughput", var v2StorageThroughputRoot])
{
    Massdex.SyncShark.V2.StorageChecksScale.RunThroughput(Path.GetFullPath(v2StorageThroughputRoot));
    return 0;
}

if (args is ["--v2-name-mapping-scale-checks"])
{
    Massdex.SyncShark.V2.RunChecks.NameMappingScale();
    return 0;
}

if (args is ["--v2-prepared-match-exclusion-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.MappingPreparedMatchExclusionAsync();
    Console.WriteLine("PASS prepared-match-exclusion");
    return 0;
}

if (args is ["--v2-linked-exclusion-triple-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.MappingLinkedExclusionTripleAsync();
    Console.WriteLine("PASS linked-exclusion-triple");
    return 0;
}

if (args is ["--v2-notice-restore-linked-exclusion-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.NoticeRestoreLinkedExclusionsAsync();
    return 0;
}

if (args is ["--v2-name-mapping-review-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.NameMappingReviewAsync();
    return 0;
}

if (args is ["--v2-name-mapping-checks"])
{
    await Massdex.SyncShark.V2.PlannerChecks.RunNameMappingAsync();
    var mappingRoot = Path.Combine(Path.GetTempPath(), "sync-shark-name-mapping-" + Guid.NewGuid().ToString("N"));
    await Massdex.SyncShark.V2.DropboxChecks.NameMappingAsync(mappingRoot);
    Massdex.SyncShark.V2.StorageChecks.NameMappingRoundTrip(mappingRoot);
    Massdex.SyncShark.V2.StorageChecks.NameMappingMigration(mappingRoot);
    return 0;
}

if (args is ["--v2-planner-checks"])
{
    await Massdex.SyncShark.V2.PlannerChecks.RunAsync();
    return 0;
}

if (args is ["--v2-slowdelete-checks", var slowDeleteRoot, var slowDeleteCase])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunSlowDeleteChecksAsync(Path.GetFullPath(slowDeleteRoot), slowDeleteCase);
    return 0;
}

if (args is ["--v2-heldperf-checks", var heldPerfRoot])
{
    var root = Path.GetFullPath(heldPerfRoot);
    Massdex.SyncShark.V2.StorageChecks.RunHeldPerformanceChecks(Path.Combine(root, "storage"));
    await Massdex.SyncShark.V2.RunChecks.RunHeldPerformanceChecksAsync();
    await Massdex.SyncShark.V2.DropboxChecks.RunFolderDeleteRecoveryChecksAsync(Path.Combine(root, "folder-delete"));
    return 0;
}

if (args is ["--v2-held-recovery"])
{
    await Massdex.SyncShark.V2.RunChecks.HeldRecoveryAsync();
    return 0;
}

if (args is ["--v2-recovery-audit", var recoveryAuditRoot])
{
    await Massdex.SyncShark.V2.ExecutorChecks.RunRecoveryAuditAsync(Path.GetFullPath(recoveryAuditRoot));
    return 0;
}

if (args is ["--v2-upload-worker-comparison"])
{
    await Massdex.SyncShark.V2.ExecutorChecks.RunUploadWorkerComparisonAsync();
    return 0;
}

if (args is ["--v2-upload-latency"])
{
    await Massdex.SyncShark.V2.ExecutorChecks.RunUploadLatencyAsync();
    return 0;
}

if (args is ["--v2-executor-checks"])
{
    await Massdex.SyncShark.V2.ExecutorChecks.RunAsync();
    return 0;
}

if (args is ["--v2-provider-followups-checks", var providerFollowupsRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.ProviderFollowupsAsync(Path.GetFullPath(providerFollowupsRoot));
    return 0;
}

if (args is ["--v2-provider-followups-checks", var providerFollowupsScenarioRoot, var providerFollowupsScenario])
{
    await Massdex.SyncShark.V2.DropboxChecks.ProviderFollowupsAsync(Path.GetFullPath(providerFollowupsScenarioRoot),
        providerFollowupsScenario);
    return 0;
}

if (args is ["--v2-dropbox-ignore-checks", var v2DropboxIgnoreRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunIgnoreChecksAsync(Path.GetFullPath(v2DropboxIgnoreRoot));
    return 0;
}

if (args is ["--v2-clientmod-checks", var clientmodRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunClientModifiedAsync(Path.GetFullPath(clientmodRoot));
    return 0;
}

if (args is ["--v2-coverage-fix-checks", var coverageFixRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunCoverageFixChecksAsync(Path.GetFullPath(coverageFixRoot));
    return 0;
}

if (args is ["--v2-coverage-checks", var coverageRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunCoverageChecksAsync(Path.GetFullPath(coverageRoot));
    return 0;
}

if (args is ["--v2-cursor-checks", var cursorRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunCursorChecksAsync(Path.GetFullPath(cursorRoot));
    return 0;
}

if (args is ["--v2-dropbox-checks", var v2DropboxRoot])
{
    await Massdex.SyncShark.V2.DropboxChecks.RunAsync(Path.GetFullPath(v2DropboxRoot));
    return 0;
}

if (args is ["--dropbox-cli-checks", var dropboxCliRoot])
{
    var cliRoot = Path.GetFullPath(dropboxCliRoot);
    await Massdex.SyncShark.DropboxCli.DropboxCliCatalogChecks.RunAsync(cliRoot);
    await Massdex.SyncShark.DropboxCli.DropboxCliVerbChecks.RunAsync(cliRoot);
    await Massdex.SyncShark.DropboxCli.DropboxCliSafetyChecks.RunAsync(cliRoot);
    await Massdex.SyncShark.DropboxCli.DropboxCliAuditFixChecks.RunAsync(cliRoot);
    return 0;
}

if (args is ["--v2-composition-checks"])
{
    await Massdex.SyncShark.V2.EngineCompositionChecks.RunAsync();
    return 0;
}

if (args is ["--v2-instrumentation-checks"])
{
    Massdex.SyncShark.V2.RunInstrumentationChecks.Run();
    return 0;
}

if (args is ["--cloud-files-offline-checks"])
{
    CloudFilesLifetimeChecks.Run();
    await CloudPlaceholderIdentityChecks.RunAsync("offline-no-files");
    return 0;
}

if (args is ["--v2-memory-checks"])
{
    await Massdex.SyncShark.V2.ObserverMemoryChecks.RunAsync();
    Massdex.SyncShark.V2.RunChecks.RunTallyMemoryChecks();
    await Massdex.SyncShark.V2.RunChecks.RunDeferredBufferCheckAsync();
    await Massdex.SyncShark.V2.ExecutorChecks.RunResultBufferChecksAsync();
    await Massdex.SyncShark.V2.RunCliChecks.RunFailureSampleChecksAsync();
    Console.WriteLine("v2 stage-one memory checks passed.");
    return 0;
}

if (args is ["--v2-review-fix-check", var finding])
{
    switch (finding)
    {
        case "F1": await Massdex.SyncShark.V2.RunCliChecks.IncludeDuringPartialFirstSyncAsync(); break;
        case "F2": await Massdex.SyncShark.V2.RunChecks.NarrowedSafetyChecksAsync(); break;
        case "F3": await Massdex.SyncShark.V2.RunCliChecks.Cli_NarrowingBatchesAndCrashAsync(); break;
        case "F4": await Massdex.SyncShark.V2.DropboxChecks.NoticeRestoreAsync(); break;
        case "F5": await Massdex.SyncShark.V2.RunChecks.InterruptedNoticeArchiveAsync(); break;
        default: throw new ArgumentException("Unknown review finding.");
    }
    return 0;
}

if (args is ["--v2-notice-archive-crash", var archiveRoot])
{
    await Massdex.SyncShark.V2.RunChecks.NoticeArchiveCrashAsync(archiveRoot);
    return 1;
}

if (args is ["--v2-notice-restore-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.NoticeRestoreChecksAsync();
    await Massdex.SyncShark.V2.DropboxChecks.NoticeRestoreAsync();
    await Massdex.SyncShark.V2.RunChecks.InterruptedNoticeArchiveAsync();
    return 0;
}

if (args is ["--v2-run-checks"])
{
    await Massdex.SyncShark.V2.RunChecks.RunAsync();
    return 0;
}

if (args is ["--v2-autosync-checks"])
{
    await Massdex.SyncShark.V2.AutoSyncChecks.RunAsync();
    return 0;
}

if (args is ["--v2-core-switch-checks"])
{
    Massdex.SyncShark.V2.CoreSwitchChecks.Run();
    return 0;
}

if (args is ["--v2-include-preview-checks"])
{
    await Massdex.SyncShark.V2.RunCliChecks.RunIncludePreviewChecksAsync();
    await Massdex.SyncShark.V2.IncludePreviewChecks.RunAsync();
    await Massdex.SyncShark.V2.DropboxChecks.IncludePreviewScopeAsync();
    return 0;
}

if (args is ["--v2-narrowing-checks"])
{
    await Massdex.SyncShark.V2.RunCliChecks.NarrowingChecksAsync();
    await Massdex.SyncShark.V2.RunChecks.NarrowedSafetyChecksAsync();
    return 0;
}

if (args is ["--v2-runcli-checks"])
{
    await Massdex.SyncShark.V2.RunCliChecks.RunAsync();
    return 0;
}

if (args is ["--online-only-return-settings-checks", var onlineOnlyReturnRoot])
{
    await OnlineOnlyReturnSettingsChecks.RunAsync(Path.GetFullPath(onlineOnlyReturnRoot));
    return 0;
}

if (args is ["--notice-options-checks", var noticeOptionsRoot])
{
    await Massdex.SyncShark.V2.SyncHostChecks.NoticeOptionsAsync();
    await Massdex.SyncShark.V2.SyncHostChecks.NoticeOptionsSqliteAsync(Path.GetFullPath(noticeOptionsRoot));
    return 0;
}

if (args is ["--v2-host-checks"])
{
    await Massdex.SyncShark.V2.SyncHostChecks.RunAsync();
    return 0;
}

if (args is ["--v2-dates-checks", var datesRoot])
{
    await Massdex.SyncShark.V2.DateRepairChecks.RunAsync(Path.GetFullPath(datesRoot));
    return 0;
}

if (args is ["--v2-usn-regression", var regression, var regressionRoot])
{
    await Massdex.SyncShark.V2.LocalJournalChecks.RegressionAsync(regression, Path.GetFullPath(regressionRoot));
    return 0;
}

if (args is ["--v2-local-journal-checks", var localJournalRoot])
{
    await Massdex.SyncShark.V2.LocalJournalChecks.RunAsync(Path.GetFullPath(localJournalRoot));
    return 0;
}

if (args is ["--v2-local-offline-checks", var offlineLocalRoot])
{
    await Massdex.SyncShark.V2.LocalChecks.RunAsync(Path.GetFullPath(offlineLocalRoot), realCloudFiles: false);
    return 0;
}

if (args is ["--v2-local-checks", var v2LocalChecksRoot])
{
    await Massdex.SyncShark.V2.LocalChecks.RunAsync(Path.GetFullPath(v2LocalChecksRoot));
    return 0;
}

if (args is ["--v2-localwatch-checks", var v2LocalWatchRoot])
{
    await Massdex.SyncShark.V2.LocalWatchChecks.RunAsync(Path.GetFullPath(v2LocalWatchRoot));
    return 0;
}

if (args is ["--v2-dropboxwatch-checks"])
{
    await Massdex.SyncShark.V2.DropboxWatchChecks.RunAsync();
    return 0;
}

if (args is ["--v2-monitor-checks", var v2MonitorChecksRoot])
{
    await Massdex.SyncShark.V2.MonitorWiringChecks.RunAsync(Path.GetFullPath(v2MonitorChecksRoot));
    return 0;
}

if (args.Length is 1 or 2 && args[0] == "--dropbox-route-checks")
{
    await Massdex.SyncShark.DropboxCli.DropboxRouteCallerChecks.RunAsync();
    Massdex.SyncShark.DropboxCli.DropboxRouteTableChecks.Run();
    return 0;
}

if (args is ["--cloud-pair-configuration-checks", var cloudPairConfigurationRoot])
{
    await CloudPairConfigurationChecks.RunAsync(Path.GetFullPath(cloudPairConfigurationRoot));
    return 0;
}

if (args is ["--cloud-pair-comparison-checks", var cloudPairComparisonRoot])
{
    await CloudPairComparisonChecks.RunAsync(Path.GetFullPath(cloudPairComparisonRoot));
    return 0;
}

if (args is ["--provider-account-migration-checks", var providerAccountMigrationRoot])
{
    await ProviderAccountMigrationChecks.RunAsync(Path.GetFullPath(providerAccountMigrationRoot));
    return 0;
}

if (args is ["--activity-index-migration-checks", var activityIndexMigrationRoot])
{
    await ActivityIndexMigrationChecks.RunAsync(Path.GetFullPath(activityIndexMigrationRoot));
    return 0;
}

if (args is ["--cloud-endpoint-observation-checks", var cloudEndpointObservationRoot])
{
    await CloudEndpointObservationChecks.RunAsync(Path.GetFullPath(cloudEndpointObservationRoot));
    return 0;
}

if (args is ["--cloud-root-hierarchy-checks", var cloudRootHierarchyRoot])
{
    await CloudRootHierarchyChecks.RunAsync(Path.GetFullPath(cloudRootHierarchyRoot));
    return 0;
}
if (args is ["--shared-cloud-object-overlap-checks", var sharedCloudObjectOverlapRoot])
{
    await SharedCloudObjectOverlapChecks.RunAsync(Path.GetFullPath(sharedCloudObjectOverlapRoot));
    return 0;
}

if (args is ["--cloud-recovery-corpus-checks", var cloudRecoveryCorpusRoot])
{
    await CloudRecoveryCorpusChecks.RunAsync(Path.GetFullPath(cloudRecoveryCorpusRoot));
    return 0;
}



if (args is ["--cloud-common-digest-checks", var cloudCommonDigestRoot])
{
    await CloudCommonDigestChecks.RunAsync(Path.GetFullPath(cloudCommonDigestRoot));
    return 0;
}

if (args is ["--cloud-content-stage-checks", var cloudContentStageRoot])
{
    await CloudContentStageChecks.RunAsync(Path.GetFullPath(cloudContentStageRoot));
    return 0;
}

if (args is ["--move-remote-uncertainty-checks", var moveRemoteUncertaintyRoot])
{
    await MoveRemoteUncertaintyChecks.RunAsync(Path.GetFullPath(moveRemoteUncertaintyRoot));
    return 0;
}

if (args is ["--dropbox-upload-progress-checks", var dropboxUploadProgressRoot])
{
    await DropboxUploadProgressChecks.RunAsync(Path.GetFullPath(dropboxUploadProgressRoot));
    return 0;
}

if (args is ["--bounded-planning-read-checks", var boundedPlanningReadRoot])
{
    await BoundedPlanningReadChecks.RunAsync(Path.GetFullPath(boundedPlanningReadRoot));
    return 0;
}

if (args is ["--scheduler-planning-checks", var schedulerPlanningRoot])
{
    await SchedulerPlanningChecks.RunAsync(Path.GetFullPath(schedulerPlanningRoot));
    return 0;
}

if (args is ["--admission-control-checks", var admissionControlRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await AdmissionControlChecks.RunAsync(Path.GetFullPath(admissionControlRoot));
    return 0;
}

if (args is ["--compact-producer-checks", var compactProducerRoot])
{
    await CompactProducerChecks.RunAsync(Path.GetFullPath(compactProducerRoot));
    return 0;
}

if (args is ["--pair-mutation-admission-checks", var pairMutationAdmissionRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await PairMutationAdmissionChecks.RunAsync(Path.GetFullPath(pairMutationAdmissionRoot));
    return 0;
}

if (args is ["--run-outcome-projection-checks", var runOutcomeProjectionRoot])
{
    await RunOutcomeProjectionChecks.RunAsync(Path.GetFullPath(runOutcomeProjectionRoot));
    return 0;
}

if (args is ["--native-move-scoped-proof-checks", var nativeMoveScopedProofRoot])
{
    await NativeMoveScopedProofChecks.RunAsync(Path.GetFullPath(nativeMoveScopedProofRoot));
    return 0;
}

if (args is ["--recovery-projection-import-checks", var recoveryProjectionImportRoot])
{
    await RecoveryProjectionImportChecks.RunAsync(Path.GetFullPath(recoveryProjectionImportRoot));
    return 0;
}

if (args is ["--presentation-snapshot-checks", var presentationSnapshotRoot])
{
    await PresentationSnapshotChecks.RunAsync(Path.GetFullPath(presentationSnapshotRoot));
    return 0;
}

if (args is ["--bounded-presentation-history-checks", var boundedPresentationHistoryRoot])
{
    await BoundedPresentationHistoryChecks.RunAsync(Path.GetFullPath(boundedPresentationHistoryRoot));
    return 0;
}

if (args is ["--normalized-original-consent-checks", var normalizedOriginalConsentRoot])
{
    await NormalizedOriginalConsentChecks.RunAsync(Path.GetFullPath(normalizedOriginalConsentRoot));
    return 0;
}

if (args is ["--explicit-work-admission-checks", var explicitWorkAdmissionRoot])
{
    await ExplicitWorkAdmissionChecks.RunAsync(Path.GetFullPath(explicitWorkAdmissionRoot));
    return 0;
}

if (args is ["--compact-observation-cut-checks", var compactObservationCutRoot])
{
    await CompactObservationCutChecks.RunAsync(Path.GetFullPath(compactObservationCutRoot));
    return 0;
}

if (args is ["--normalized-rewind-state-checks", var normalizedRewindStateRoot])
{
    await NormalizedRewindStateChecks.RunAsync(Path.GetFullPath(normalizedRewindStateRoot));
    return 0;
}

if (args is ["--compact-retry-consent-checks", var compactRetryConsentRoot])
{
    await CompactRetryConsentChecks.RunAsync(Path.GetFullPath(compactRetryConsentRoot));
    return 0;
}

if (args is ["--ordered-endpoint-mode-checks", var orderedEndpointModeRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native ordered endpoint mode checks require Windows.");
    await OrderedEndpointModeChecks.RunAsync(Path.GetFullPath(orderedEndpointModeRoot));
    return 0;
}

if (args is ["--sparse-planning-context-checks", var sparsePlanningContextRoot])
{
    await SparsePlanningContextChecks.RunAsync(Path.GetFullPath(sparsePlanningContextRoot));
    return 0;
}

if (args is ["--cloud-create-execution-checks", var cloudCreateExecutionRoot])
{
    await CloudCreateExecutionChecks.RunAsync(Path.GetFullPath(cloudCreateExecutionRoot));
    return 0;
}

if (args is ["--cloud-pair-host-binding-checks", var cloudPairHostBindingRoot])
{
    await CloudPairHostBindingChecks.RunAsync(Path.GetFullPath(cloudPairHostBindingRoot));
    return 0;
}

if (args is ["--conditional-directory-delete-checks", var conditionalDirectoryDeleteRoot])
{
    await ConditionalDirectoryDeleteChecks.RunAsync(Path.GetFullPath(conditionalDirectoryDeleteRoot));
    return 0;
}

if (args is ["--google-hint-scheduling-checks", var googleHintRoot])
{
    await GoogleHintSchedulingChecks.RunAsync(Path.GetFullPath(googleHintRoot));
    return 0;
}

if (args is ["--cloud-runner-integration-checks", var cloudRunnerIntegrationRoot])
{
    await CloudRunnerIntegrationChecks.RunAsync(Path.GetFullPath(cloudRunnerIntegrationRoot));
    return 0;
}

if (args is ["--reviewed-cloud-comparison-checks", var reviewedCloudRoot])
{
    await CloudRunnerIntegrationChecks.RunReviewedComparerAsync(reviewedCloudRoot);
    return 0;
}

if (args is ["--cloud-runner-refusal-tail", var cloudRunnerTailRoot])
{
    await CloudRunnerIntegrationChecks.RunAsync(Path.GetFullPath(cloudRunnerTailRoot), remainingCasesOnly: true);
    return 0;
}

if (args is ["--cloud-update-execution-checks", var cloudUpdateExecutionRoot])
{
    await CloudUpdateExecutionChecks.RunAsync(Path.GetFullPath(cloudUpdateExecutionRoot));
    return 0;
}

if (args is ["--cloud-recovery-consumer-checks", var cloudRecoveryConsumerRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await CloudRecoveryConsumerChecks.RunAsync(Path.GetFullPath(cloudRecoveryConsumerRoot));
    return 0;
}

if (args is ["--cloud-recovery-restore-checks", var cloudRecoveryRestoreRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await CloudRecoveryRestoreChecks.RunAsync(Path.GetFullPath(cloudRecoveryRestoreRoot));
    return 0;
}

if (args is ["--cloud-delete-execution-checks", var cloudDeleteExecutionRoot])
{
    await CloudDeleteExecutionChecks.RunAsync(Path.GetFullPath(cloudDeleteExecutionRoot));
    return 0;
}

if (args is ["--cloud-move-execution-checks", var cloudMoveExecutionRoot])
{
    await CloudMoveExecutionChecks.RunAsync(Path.GetFullPath(cloudMoveExecutionRoot));
    return 0;
}

if (args is ["--cloud-conflict-execution-checks", var cloudConflictExecutionRoot])
{
    await CloudConflictExecutionChecks.RunAsync(Path.GetFullPath(cloudConflictExecutionRoot));
    return 0;
}

if (args is ["--cloud-pair-authoring-checks", var cloudPairAuthoringRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await CloudPairAuthoringChecks.RunAsync(Path.GetFullPath(cloudPairAuthoringRoot));
    return 0;
}

if (args is ["--pair-authoring-recovery-checks", var pairAuthoringRecoveryRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await PairAuthoringRecoveryChecks.RunAsync(Path.GetFullPath(pairAuthoringRecoveryRoot));
    return 0;
}
if (args is ["--retained-control-recovery-checks", var retainedControlRecoveryRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await RetainedControlRecoveryChecks.RunAsync(Path.GetFullPath(retainedControlRecoveryRoot));
    return 0;
}

if (args is ["--retained-control-recovery-cli-checks"])
{
    await RetainedControlRecoveryCliChecks.RunAsync();
    return 0;
}

if (args is ["--atomic-edit-boundary-checks", var atomicEditBoundaryRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await AtomicEditBoundaryChecks.RunAsync(Path.GetFullPath(atomicEditBoundaryRoot));
    return 0;
}

if (args is ["--opaque-hierarchy-retention-checks", var opaqueHierarchyRetentionRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await OpaqueHierarchyRetentionChecks.RunAsync(Path.GetFullPath(opaqueHierarchyRetentionRoot));
    return 0;
}

if (args is ["--local-endpoint-exact-lookup-checks", var exactLocalLookupRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native exact lookup requires Windows.");
    await LocalEndpointExactLookupChecks.RunAsync(Path.GetFullPath(exactLocalLookupRoot), CreateDirectoryJunction);
    return 0;
}

if (args is ["--cloud-public-pair-authoring-checks", var endpointModeSubsetRoot, "endpoint-mode-subset"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native endpoint matrix requires Windows.");
    await CloudPublicPairAuthoringChecks.RunEndpointModeSubsetAsync(Path.GetFullPath(endpointModeSubsetRoot));
    return 0;
}

if (args is ["--cloud-public-pair-authoring-checks", var endpointModeMatrixRoot, "endpoint-mode-matrix"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native endpoint matrix requires Windows.");
    await CloudPublicPairAuthoringChecks.RunEndpointModeMatrixAsync(Path.GetFullPath(endpointModeMatrixRoot));
    return 0;
}

if (args is ["--cloud-public-pair-authoring-checks", var localPairReplacementRoot, "local-pair-replacement"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native local replacement requires Windows.");
    await CloudPublicPairAuthoringChecks.RunLocalReplacementChecksAsync(Path.GetFullPath(localPairReplacementRoot), localOnly: true);
    return 0;
}

if (args is ["--cloud-public-pair-authoring-checks", var localReplacementRoot, "local-replacement"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native pair binding requires Windows.");
    await CloudPublicPairAuthoringChecks.RunLocalReplacementChecksAsync(Path.GetFullPath(localReplacementRoot));
    return 0;
}

if (args is ["--cloud-public-pair-authoring-checks", var cloudPublicPairAuthoringRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await CloudPublicPairAuthoringChecks.RunAsync(Path.GetFullPath(cloudPublicPairAuthoringRoot));
    return 0;
}

if (args is ["--recovered-host-entry-checks", var recoveredHostEntryRoot])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture requires Windows.");
    await CloudPublicPairAuthoringChecks.RunRecoveredHostEntryAsync(Path.GetFullPath(recoveredHostEntryRoot));
    return 0;
}

if (args is ["--endpoint-context-capture-checks", var endpointContextCaptureRoot])
{
    await EndpointContextCaptureChecks.RunAsync(Path.GetFullPath(endpointContextCaptureRoot));
    return 0;
}

if (args is ["--endpoint-activity-presentation-checks", var endpointActivityRoot])
{
    await EndpointActivityPresentationChecks.RunAsync(Path.GetFullPath(endpointActivityRoot));
    return 0;
}

if (args is ["--run-transfer-summary-checks", var runTransferSummaryRoot])
{
    await RunTransferSummaryChecks.RunAsync(Path.GetFullPath(runTransferSummaryRoot));
    await CoordinatedTransferSummaryContract(Path.GetFullPath(runTransferSummaryRoot));
    return 0;
}

if (args is ["--endpoint-history-projection-checks", var endpointHistoryProjectionRoot])
{
    await EndpointHistoryProjectionChecks.RunAsync(Path.GetFullPath(endpointHistoryProjectionRoot));
    return 0;
}







if (args is ["--pause-contract-checks", var pauseContractRoot])
{
    await PauseContractChecks.RunAsync(Path.GetFullPath(pauseContractRoot));
    return 0;
}

if (args is ["--cli-run-control-checks", var cliRunControlRoot])
{
    return await CliRunControlChecks.RunAsync(Path.GetFullPath(cliRunControlRoot));
}

if (args is ["--authorization-cancellation-checks", var authorizationCancellationRoot])
{
    await AuthorizationCancellationChecks.RunAsync(Path.GetFullPath(authorizationCancellationRoot));
    return 0;
}

if (args is ["--cli-surface-checks", var cliSurfaceRoot])
{
    return await CliSurfaceChecks.RunAsync(Path.GetFullPath(cliSurfaceRoot));
}

if (args is ["--cli-sharing-checks", var cliSharingRoot])
{
    return await CliSharingChecks.RunAsync(Path.GetFullPath(cliSharingRoot));
}

if (args is ["--cli-presentation-checks"])
{
    CliChecks.RunPresentationChecks();
    return 0;
}

if (args is ["--exact-file-upload-checks", var exactFileUploadRoot])
{
    await ExactFileUploadChecks.RunAsync(Path.GetFullPath(exactFileUploadRoot));
    return 0;
}

if (args is ["--installed-identity-checks"])
{
    InstalledRuntimeIdentityChecks.Run();
    return 0;
}

if (args is ["--device-space-checks"])
{
    StorageInventoryChecks.DeviceSpaceChecks();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SyncStorageInventory.ReadDeviceSpace()));
    return 0;
}

if (args is ["--cli-checks"] or ["--cli-checks", _] or ["--cli-checks", _, _])
{
    var cliCheckRoot = args.Length >= 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(Path.GetTempPath(), "sync-shark-cli-checks", Guid.NewGuid().ToString("N"));
    await CliChecks.RunAsync(cliCheckRoot, args.Length == 3 ? Path.GetFullPath(args[2]) : null);
    return 0;
}

if (args is ["--directory-transfer-scheduler-checks"])
{
    await ConcurrentTransferChecks.RunDirectorySchedulerChecksAsync();
    return 0;
}

if (args is ["--directory-first-sync-cli-checks", var directoryCliRoot])
{
    await FirstSyncEffectChecks.RunDirectoryConcurrencyAsync(Path.GetFullPath(directoryCliRoot));
    return 0;
}

if (args is ["--concurrent-transfer-checks", var concurrentTransferRoot])
{
    await TransferThroughputAndRateLimitContract();
    await DropboxDownloadRetryChecks.RunAsync();
    await ConcurrentTransferChecks.RunAsync(Path.GetFullPath(concurrentTransferRoot));
    return 0;
}

if (args is ["--concurrent-transfer-checks", var scaleTransferRoot, var filesPerSide])
{
    await TransferThroughputAndRateLimitContract();
    await ConcurrentTransferChecks.RunAsync(Path.GetFullPath(scaleTransferRoot), int.Parse(filesPerSide));
    return 0;
}

if (args is ["--concurrent-cli-transfer-checks", var concurrentCliRoot])
{
    await ConcurrentTransferChecks.RunCliAsync(Path.GetFullPath(concurrentCliRoot));
    return 0;
}

if (args is ["--concurrent-transfer-checks", var concurrencyRoot, var concurrencyFiles, var concurrencyLimit])
{
    await TransferThroughputAndRateLimitContract();
    await ConcurrentTransferChecks.RunAsync(Path.GetFullPath(concurrencyRoot), int.Parse(concurrencyFiles), int.Parse(concurrencyLimit));
    return 0;
}

if (args is ["--concurrent-cli-transfer-checks", var cliConcurrencyRoot, var cliConcurrencyFiles, var cliConcurrencyLimit])
{
    await ConcurrentTransferChecks.RunCliAsync(Path.GetFullPath(cliConcurrencyRoot), int.Parse(cliConcurrencyFiles), int.Parse(cliConcurrencyLimit));
    return 0;
}

if (args is ["--first-sync-policy-handoff-checks", var policyHandoffRoot])
{
    await FirstSyncEffectChecks.RunPolicyHandoffAsync(Path.GetFullPath(policyHandoffRoot));
    return 0;
}

if (args is ["--reviewed-source-refresh-checks", var refreshRoot])
{
    await ReviewedSourceRefreshChecks.RunAsync(Path.GetFullPath(refreshRoot));
    return 0;
}

if (args is ["--current-copy-move-proof-checks", var currentCopyProofRoot])
{
    await CompletedCopyMoveChecks.RunAsync(Path.GetFullPath(currentCopyProofRoot), normalizedProofOnly: true);
    return 0;
}

if (args is ["--completed-copy-move-checks", var copyMoveRoot])
{
    await CompletedCopyMoveChecks.RunAsync(Path.GetFullPath(copyMoveRoot));
    return 0;
}

if (args is ["--cloned-state-journey", var clonedStateJourneyRoot])
    return await RunClonedStateJourney(clonedStateJourneyRoot, allowEffects: false);
if (args is ["--cloned-state-journey", var clonedStateJourneyEffectRoot, "--journey-effects"])
    return await RunClonedStateJourney(clonedStateJourneyEffectRoot, allowEffects: true);
if (args is ["--cloned-state-journey", var clonedRetryRoot, "--resume-retained-retries"])
    return await RunClonedStateJourney(clonedRetryRoot, allowEffects: true, resumeRetained: true);
if (args is ["--cloned-state-journey", var clonedReviewRoot, "--complete-retained-review"])
    return await RunClonedStateJourney(clonedReviewRoot, allowEffects: true, resumeRetained: true,
        completeRetainedReview: true);
if (args is ["--cloned-state-journey", var mixedCloneRoot, "--mixed-overlap"])
    return await RunClonedStateJourney(mixedCloneRoot, allowEffects: true, resumeRetained: true,
        mixedOverlap: true);

// R697-ISOLATED-CLONED-JOURNEY step 0: bind the retained clone and report its exact retained identity and
// roots BEFORE any endpoint effect. Read-only over the clone's own database; no production path, no provider,
// no credential, no IPC. Later steps add the HostWire provider transport, the journey command allowlist and
// the isolated rebind; this step is the safe precondition every later step depends on.
static async Task<int> RunClonedStateJourney(string cloneRoot, bool allowEffects, bool resumeRetained = false,
    bool completeRetainedReview = false, bool mixedOverlap = false)
{
    var ownerAllocation = Path.Combine(DiagnosticPaths.RequireRoot(), "build");
    var allowedClone = Path.Combine(ownerAllocation, "root682-cloned-state-live", "live-state-clone");
    var rejectedAliasRoot = Path.Combine(ownerAllocation, "root682-cloned-state-selftest");
    var full = Path.GetFullPath(cloneRoot);
    var allocation = Path.GetFullPath(ownerAllocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var rejected = Path.GetFullPath(rejectedAliasRoot);
    if (string.Equals(full, rejected, StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(rejected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException(
            "Refused before any traversal or open: the journey never opens the held selftest live-alias root " + rejected);
    if (!string.Equals(full, Path.GetFullPath(allowedClone), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The journey opens only the already-verified retained clone: " + allowedClone);
    if (!full.StartsWith(allocation, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The journey clone must live inside the owned build allocation: " + allocation);
    for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        if (current.Exists && (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new InvalidDataException("The journey clone crosses a reparse point: " + current.FullName);
    var database = Path.Combine(full, "vNext", "state", "syncshark.db");
    if (!File.Exists(database)) throw new InvalidDataException("The journey clone carries no authority database.");
    if ((File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Refused: the clone database member itself is a reparse/hardlink alias: " + database);
    // Every path component between the allowed clone and the database must be alias-free BEFORE any open.
    var allowedFull = Path.GetFullPath(allowedClone);
    for (var component = new DirectoryInfo(Path.GetDirectoryName(database)!); component is not null;
         component = component.Parent)
    {
        if (component.Exists && (component.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new InvalidDataException("Refused: a clone path component is a reparse point or offline: " +
                                           component.FullName);
        if (component.Exists)
        {
            // Stable native DIRECTORY handle through the existing seam; directories keep the ReparsePoint/Offline
            // rule rather than the regular-file NumberOfLinks rule.
            using var stableDirectory = PilotDirectoryLease.Open(component.FullName);
        }
        if (string.Equals(component.FullName, allowedFull, StringComparison.OrdinalIgnoreCase)) break;
    }
    // Reuse the existing native guard: a mutable state FILE must have exactly one NTFS link and its own final
    // path. Directories are NOT held to the regular-file link rule, so they keep the attribute check above.
    static void RequireSingleLinkFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new IOException("Could not read the native file identity for " + path);
        if ((info.FileAttributes & FileAttributes.Directory) != 0 || PilotPath.IsUnsafeAttributes(info.FileAttributes))
            throw new IOException("Refused: not a regular on-disk file: " + path);
        if (info.NumberOfLinks != 1)
            throw new IOException("Refused: the clone member has " + info.NumberOfLinks +
                                   " NTFS links (hard-link alias): " + path);
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refused: the clone member resolves to a different final path: " + path);
    }
    RequireSingleLinkFile(database);
    foreach (var cloneSidecar in new[] { database + "-wal", database + "-shm" })
        if (File.Exists(cloneSidecar)) RequireSingleLinkFile(cloneSidecar);
    using var connection = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False");
    connection.Open();
    var pairRows = 0;
    using (var query = connection.CreateCommand())
    {
        query.CommandText = "SELECT count(*) FROM sync_pair;";
        pairRows = Convert.ToInt32(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    // Step 1: open the SAME clone through the real product Host over an in-process provider transport. The
    // transport (HostWire) answers only its seeded provider objects and the credential store returns a
    // synthetic token, so nothing here reaches the network or the OS credential store.
    Directory.CreateDirectory(Path.Combine(full, "state"));
    using var stateLock = PilotInstanceLock.Acquire(Path.Combine(full, "state", "instance.lock"));
    var providerWire = new CloudPairHostBindingChecks.HostWire();
    using var journeyHttp = new HttpClient(providerWire, disposeHandler: true);
    var journeyDatabase = new SyncAuthorityDatabase(database);
    using var journeyHost = new SyncAuthorityRuntimeHost(stateLock.StateAuthority, journeyHttp,
        new SyncPairStore(stateLock.StateAuthority), new SyncRuntimeStateStore(stateLock.StateAuthority),
        new DropboxConnectionStore(stateLock.StateAuthority), new CloudPairHostBindingChecks.FixtureDropboxCredentials(),
        journeyDatabase, googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
        googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(providerWire));
    var journeyPairs = (await journeyHost.LoadPairsAsync()).Pairs;
    var verdicts = journeyPairs.Select(pair => new
    {
        pair_id = pair.Id,
        name = pair.Name,
        local_root = pair.LocalRoot,
        local_inside_owned_allocation = Path.GetFullPath(pair.LocalRoot).StartsWith(allocation, StringComparison.OrdinalIgnoreCase),
        cloud_root = pair.DropboxRoot,
        recovery_root = pair.RecoveryRoot?.RootPath,
        connection_id = pair.ConnectionId,
        provider = pair.Provider,
        policy_revision = pair.PolicyRevision
    }).ToArray();
    var rootsOwned = verdicts.All(item => item.local_inside_owned_allocation);
    // Root sshark:755-r1 - READ-ONLY exact lookup of the originally bound pending operation, parameterised by
    // its explicit id. No ORDER BY latest fallback, no new apply for discovery.
    List<Dictionary<string, string?>> ReadRows(SqliteConnection readConnection, string sql,
        params (string Name, string Value)[] readParameters)
    {
        var readResult = new List<Dictionary<string, string?>>();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = sql;
        foreach (var readParameter in readParameters) readCommand.Parameters.AddWithValue(readParameter.Name, readParameter.Value);
        using var readReader = readCommand.ExecuteReader();
        while (readReader.Read())
        {
            var readRow = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var readIndex = 0; readIndex < readReader.FieldCount; readIndex++)
                readRow[readReader.GetName(readIndex)] = readReader.IsDBNull(readIndex)
                    ? null : Convert.ToString(readReader.GetValue(readIndex), System.Globalization.CultureInfo.InvariantCulture);
            readResult.Add(readRow);
        }
        return readResult;
    }
    const string boundOperationId = "operation-f416415bc7b607062a3d567180a832b2";
    // Per-path accepted-effect observability from the product's OWN retained evidence: known-success effect
    // receipts for this pair. No JSON substring and no HTTP request counting.
    long ReadAcceptedReceiptCount(string databasePath, string pairId)
    {
        using var receiptConnection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadOnly;Pooling=False");
        receiptConnection.Open();
        using var receiptCommand = receiptConnection.CreateCommand();
        receiptCommand.CommandText =
            "SELECT count(*) FROM effect_receipt receipt " +
            "JOIN operation_attempt attempt ON attempt.attempt_id=receipt.attempt_id " +
            "JOIN operation operation ON operation.operation_id=attempt.operation_id " +
            "WHERE operation.pair_id=$pair AND receipt.certainty='known_success';";
        receiptCommand.Parameters.AddWithValue("$pair", pairId);
        return Convert.ToInt64(receiptCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    using (var boundLookup = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False"))
    {
        boundLookup.Open();
        var boundOperationRows = ReadRows(boundLookup, "SELECT * FROM operation WHERE operation_id=$op;", ("$op", boundOperationId));
        var boundMemberRows = ReadRows(boundLookup, "SELECT * FROM operation_member WHERE operation_id=$op;", ("$op", boundOperationId));
        var boundAttemptRows = ReadRows(boundLookup, "SELECT * FROM operation_attempt WHERE operation_id=$op;", ("$op", boundOperationId));
        var boundReceiptRows = ReadRows(boundLookup,
            "SELECT count(*) AS receipts FROM effect_receipt receipt JOIN operation_attempt attempt " +
            "ON attempt.attempt_id=receipt.attempt_id WHERE attempt.operation_id=$op;", ("$op", boundOperationId));
        var boundRunRows = ReadRows(boundLookup,
            "SELECT DISTINCT json_extract(detail_json,'$.RunId') AS run_id FROM history_event " +
            "WHERE event_kind='presentation' AND json_valid(detail_json) " +
            "AND json_extract(detail_json,'$.OperationId')=$op;", ("$op", boundOperationId));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "r697-bound-operation-lookup/1", bound_operation = boundOperationId,
            operation_rows = boundOperationRows, member_rows = boundMemberRows, attempt_rows = boundAttemptRows,
            receipt_rows = boundReceiptRows, linked_runs = boundRunRows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    if (!allowEffects)
    {
        // Read-only diagnostic: every nonterminal operation for the pair, its attempts and its owning run(s).
        using (var staleLookup = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False"))
        {
            staleLookup.Open();
            var nonterminal = ReadRows(staleLookup,
                "SELECT o.operation_id, o.kind, o.status, o.operation_group_id, o.created_utc, " +
                "(SELECT group_concat(m.role || '=' || coalesce(m.expected_path_key,'')) FROM operation_member m " +
                " WHERE m.operation_id=o.operation_id) AS members " +
                "FROM operation o WHERE o.pair_id=$pair AND o.status NOT IN ('completed','superseded') " +
                "ORDER BY o.rowid;", ("$pair", "1fef83aa278d9b92ef27"));
            var attempts = ReadRows(staleLookup,
                "SELECT a.operation_id, a.attempt_id, a.status AS attempt_status, a.effect_kind, " +
                "(SELECT count(*) FROM effect_receipt r WHERE r.attempt_id=a.attempt_id) AS receipts " +
                "FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id " +
                "WHERE o.pair_id=$pair AND o.status NOT IN ('completed','superseded') ORDER BY a.rowid;",
                ("$pair", "1fef83aa278d9b92ef27"));
            var owners = ReadRows(staleLookup,
                "SELECT DISTINCT json_extract(h.detail_json,'$.RunId') AS run_id, " +
                "json_extract(h.detail_json,'$.OperationId') AS operation_id " +
                "FROM history_event h JOIN operation o ON o.operation_id=json_extract(h.detail_json,'$.OperationId') " +
                "WHERE h.event_kind='presentation' AND json_valid(h.detail_json) AND o.pair_id=$pair " +
                "AND o.status NOT IN ('completed','superseded');", ("$pair", "1fef83aa278d9b92ef27"));
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "r697-nonterminal-operations/1",
                nonterminal_operations = nonterminal, attempts, owning_runs = owners },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PARTIAL cloned-state journey (read-only): host opened and the originally bound " +
                          "operation was looked up by explicit id; the apply/retry effects path was NOT started " +
                          "(pass --journey-effects to enable it)");
        return 3;
    }
    var steps = new List<object>();
    var complete = false;
    string? firstFailure = null;
    (long Db, long Wal, long Shm) Sides(string path)
    {
        long Size(string candidate) => File.Exists(candidate) ? new FileInfo(candidate).Length : -1;
        return (Size(path), Size(path + "-wal"), Size(path + "-shm"));
    }
    async Task<(int Exit, string Out, string Err)> JourneyCli(params string[] cliArgs)
    {
        var journeyOut = new StringWriter();
        var journeyErr = new StringWriter();
        var journeyOperations = new FirstSyncEffectChecks.FixtureOperations(journeyHost, stateLock.StateAuthority);
        var journeyExit = await SyncCli.ExecuteAsync(cliArgs, journeyOut, journeyErr, () => journeyOperations);
        return (journeyExit, journeyOut.ToString(), journeyErr.ToString());
    }
    (long Db, long Wal, long Shm) finalSides = (0, 0, 0);
    var settled = false;
    try
    {
        if (journeyPairs.Count != 1 && !(mixedOverlap && journeyPairs.Count == 2))
            throw new InvalidDataException("The journey rebinds exactly one retained published pair; found " + journeyPairs.Count);
        var retainedPair = journeyPairs.Single(pair => pair.Id == "1fef83aa278d9b92ef27");
        var isolatedRoot = Path.Combine(full, "journey", "local");
        Directory.CreateDirectory(isolatedRoot);
        if ((File.GetAttributes(isolatedRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refused: the isolated local root is a reparse point: " + isolatedRoot);
        using var isolatedLease = PilotDirectoryLease.Open(isolatedRoot);
        steps.Add(new { step = "isolated_root", path = isolatedRoot, volume = isolatedLease.Identity.VolumeSerialNumber,
            file_id = isolatedLease.Identity.FileId });
        var connectionStore = new DropboxConnectionStore(stateLock.StateAuthority);
        DropboxConnectionBinding? binding;
        try
        {
            binding = connectionStore.Load(retainedPair.ConnectionId);
        }
        catch (Exception connectionError)
        {
            throw new InvalidDataException("Loading the retained connection " + retainedPair.ConnectionId +
                " failed: " + connectionError.GetType().Name + ": " + connectionError.Message, connectionError);
        }
        if (binding is null)
            throw new InvalidDataException("The retained Dropbox connection " + retainedPair.ConnectionId +
                " is absent from the clone.");
        steps.Add(new { step = "retained_binding", connection_id = binding.ConnectionId,
            account_id = binding.AccountId, root_namespace = binding.RootNamespaceId });
        // Seed ONLY the retained cloud identity into the in-process transport; every other request is refused
        // by HostWire itself, and no real network or OS credential is reached.
        providerWire.AddDropboxFolder(binding, retainedPair.DropboxRoot, retainedPair.DropboxFolderId ?? "");
        if (retainedPair.RecoveryRoot is { } retainedRecovery)
            providerWire.AddDropboxFolder(binding, retainedRecovery.RootPath, retainedRecovery.RootFolderId);
        steps.Add(new { step = "wire_seeded", provider_requests = providerWire.TotalRequests });
        var retainedOwned = Path.GetFullPath(retainedPair.LocalRoot)
            .StartsWith(allocation, StringComparison.OrdinalIgnoreCase);
        SyncPair rebound;
        if (retainedOwned)
        {
            // Multi-attempt reuse: an earlier attempt already moved this pair onto the owned isolated root, and
            // a second pairs edit is refused while that attempt's work is pending. Skipping is the honest path.
            rebound = retainedPair;
            steps.Add(new { step = "rebind", skipped = true,
                reason = "the pair already resolves to an owned isolated root", local_root = rebound.LocalRoot,
                local_inside_owned_allocation = true });
        }
        else
        {
            var rebind = await JourneyCli("--cli", "pairs", "edit", "--pair", retainedPair.Id, "--account",
                retainedPair.ConnectionId, "--generation",
                retainedPair.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--settings", "{\"direction\":\"TwoWay\"}", "--local", isolatedRoot, "--enable", "--yes", "--json");
            rebound = (await journeyHost.LoadPairsAsync()).Pairs.Single(item => item.Id == retainedPair.Id);
            steps.Add(new { step = "rebind", exit = rebind.Exit, local_root = rebound.LocalRoot,
                local_inside_owned_allocation = Path.GetFullPath(rebound.LocalRoot).StartsWith(allocation, StringComparison.OrdinalIgnoreCase),
                provider_requests = providerWire.TotalRequests,
                error = rebind.Exit == 0 ? null : rebind.Err.Trim() });
        }
        var reboundOwned = Path.GetFullPath(rebound.LocalRoot).StartsWith(allocation, StringComparison.OrdinalIgnoreCase);
        if (!reboundOwned)
        {
            firstFailure = "rebind did not move the pair onto an owned isolated root";
        }
        else
        {
            if (resumeRetained)
            {
                bool IsRetainedJourneyPath(string path) =>
                    !path.Contains('/') && (path == "clash.txt" || path.StartsWith("journey-", StringComparison.Ordinal)) ||
                    mixedOverlap && (path is "Child/journey-mixed-download.txt" or "Child/journey-mixed-conflict.txt");
                if (rebound.Id != "1fef83aa278d9b92ef27" ||
                    !string.Equals(Path.GetFullPath(rebound.LocalRoot), isolatedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Retained retry proof requires the exact already-isolated fixture pair.");
                var seeds = ReadRows(connection, """
                    SELECT m.expected_path_key AS path,r.provider_metadata_json AS metadata,
                        v.content_hash AS hash,v.size_bytes AS size
                    FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id
                    JOIN operation o ON o.operation_id=a.operation_id
                    JOIN operation_member m ON m.operation_id=o.operation_id AND m.role='source'
                    JOIN object_version v ON v.object_version_id=m.object_version_id
                    WHERE o.pair_id=$pair AND o.kind IN ('create_remote','update_remote') AND o.status='completed'
                      AND r.certainty='known_success' AND NOT EXISTS(
                        SELECT 1 FROM operation newer JOIN operation_member nm ON nm.operation_id=newer.operation_id
                          AND nm.role='source' WHERE newer.pair_id=o.pair_id
                          AND newer.kind IN ('create_remote','update_remote') AND newer.status='completed'
                          AND nm.expected_path_key=m.expected_path_key AND newer.rowid>o.rowid)
                    ORDER BY r.rowid;
                    """, ("$pair", rebound.Id));
                foreach (var seed in seeds)
                {
                    var relative = PilotPath.NormalizeRelative(seed["path"]!);
                    if (!IsRetainedJourneyPath(relative))
                        throw new InvalidDataException("Fixture restoration refuses a non-journey file.");
                    var source = Path.Combine(isolatedRoot, relative);
                    RequireSingleLinkFile(source);
                    var bytes = await File.ReadAllBytesAsync(source);
                    if (bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture) != seed["size"] ||
                        DropboxContentHash.Compute(bytes) != seed["hash"])
                        throw new InvalidDataException("Retained fixture source bytes changed: " + relative);
                    using var metadata = JsonDocument.Parse(seed["metadata"]!);
                    var receipt = metadata.RootElement;
                    // Create and conditional-update receipts retain their own serializer
                    // field casing. Preserve either exact receipt, without inventing identity.
                    var fileId = receipt.TryGetProperty("fileId", out var lowerId)
                        ? lowerId.GetString() : receipt.GetProperty("FileId").GetString();
                    var revision = receipt.TryGetProperty("revision", out var lowerRevision)
                        ? lowerRevision.GetString() : receipt.GetProperty("Revision").GetString();
                    providerWire.RestoreDropboxFixtureFile(binding, rebound.DropboxRoot.TrimEnd('/') + "/" + relative,
                        fileId!, revision!, seed["hash"]!, bytes);
                }
                // A restarted synthetic provider must also restore earlier completed downloads,
                // not invent their deletion. Bind their exact retained source identity and bytes.
                var downloadedSeeds = ReadRows(connection, """
                    SELECT m.expected_path_key AS path,v.identity_json AS identity,v.content_hash AS hash,v.size_bytes AS size
                    FROM operation o JOIN operation_member m ON m.operation_id=o.operation_id AND m.role='source'
                    JOIN object_version v ON v.object_version_id=m.object_version_id
                    WHERE o.pair_id=$pair AND o.kind IN ('create_local','update_local') AND o.status='completed'
                      AND NOT EXISTS(SELECT 1 FROM operation newer JOIN operation_member nm ON nm.operation_id=newer.operation_id
                        AND nm.role='source' WHERE newer.pair_id=o.pair_id
                        AND newer.kind IN ('create_local','update_local','create_remote','update_remote')
                        AND newer.status='completed' AND nm.expected_path_key=m.expected_path_key AND newer.rowid>o.rowid);
                    """, ("$pair", rebound.Id));
                foreach (var seed in downloadedSeeds)
                {
                    var path = PilotPath.NormalizeRelative(seed["path"]!);
                    if (!IsRetainedJourneyPath(path))
                        throw new InvalidDataException("Fixture restoration refuses an unrelated downloaded file.");
                    var source = Path.Combine(isolatedRoot, path);
                    RequireSingleLinkFile(source);
                    var bytes = await File.ReadAllBytesAsync(source);
                    if (path == "clash.txt" && DropboxContentHash.Compute(bytes) != seed["hash"])
                        bytes = Encoding.UTF8.GetBytes("journey remote clash payload");
                    if (bytes.LongLength.ToString() != seed["size"] || DropboxContentHash.Compute(bytes) != seed["hash"])
                        throw new InvalidDataException("Retained downloaded fixture bytes changed: " + path);
                    using var identity = JsonDocument.Parse(seed["identity"]!);
                    providerWire.RestoreDropboxFixtureFile(binding, rebound.DropboxRoot.TrimEnd('/') + "/" + path,
                        identity.RootElement.GetProperty("fileId").GetString()!,
                        identity.RootElement.GetProperty("revision").GetString()!, seed["hash"]!, bytes);
                }
                if (mixedOverlap)
                {
                    await MixedClonedJourneyChecks.RunAsync(journeyHost, journeyDatabase, providerWire,
                        binding, rebound, isolatedRoot, JourneyCli);
                    complete = true;
                }
                else
                {
                var retainedRetries = new[]
                {
                    (Run: "command-d6f90d1a5e7f4682bff81709665884b4", Operation: "operation-779b9c6265b36c379a2a903a01ac43c3", Path: "journey-fail-1791571766.txt"),
                    (Run: "command-bbbdd7abe8a149168352debdc71d38d4", Operation: "operation-a2f1a43265b527d058b6eef2c64e7f08", Path: "journey-fail-1791572666.txt"),
                    (Run: "command-cc3fa59e4b264c238b903a3984210c96", Operation: "operation-fafee0702dc3beed42accd60aad1bb2a", Path: "journey-fail-1791573296.txt"),
                    (Run: "command-9a42907b9c4a4079acc9ab09f6f6c096", Operation: "operation-b28a5f76a8c230014079d0c807158a01", Path: "journey-fail-1791576013.txt")
                };
                foreach (var target in retainedRetries)
                {
                    var preview = await journeyHost.LoadRunRetryAsync(target.Run, selectedPairId: rebound.Id);
                    // First-sync approval deliberately closes old retry menus. On a
                    // completion-only restart, prove the retained known-success bytes
                    // instead of demanding that superseded approval remain executable.
                    if (completeRetainedReview && rebound.InitialReviewApproved == true)
                    {
                        if (preview.Items.Count != 0 || preview.UnavailableReason !=
                            "First sync is already complete. Run a fresh normal sync; completed transfers will not be replayed." ||
                            !seeds.Any(seed => seed["path"] == target.Path))
                            throw new InvalidDataException("Approved fixture lacks its exact completed retry evidence: " + target.Path);
                        steps.Add(new { step = "retained_retry", run = target.Run, path = target.Path,
                            already_complete = true, known_success_receipt_and_bytes_verified = true,
                            original_approval_closed = true, effects_replayed = false });
                        continue;
                    }
                    var eligible = preview.Items.SingleOrDefault(item => item.Path == target.Path)
                        ?? throw new InvalidDataException("Retained retry path is unavailable: " + target.Path + ": " + preview.UnavailableReason);
                    if (eligible.State == "Completed")
                    {
                        steps.Add(new { step = "retained_retry", run = target.Run, path = target.Path,
                            already_complete = true, effects_replayed = false });
                        continue;
                    }
                    if (!eligible.CanRetry || eligible.OperationId != target.Operation || preview.UnavailableReason is not null)
                        throw new InvalidDataException("Exact retained retry is not eligible: " + target.Path);
                    var source = Path.Combine(isolatedRoot, target.Path);
                    RequireSingleLinkFile(source);
                    var expectedBytes = await File.ReadAllBytesAsync(source);
                    var before = ReadAcceptedReceiptCount(database, rebound.Id);
                    var verification = await JourneyCli("--cli", "run", "verify", "--run", target.Run, "--yes", "--json");
                    if (verification.Exit != 0 || before != ReadAcceptedReceiptCount(database, rebound.Id))
                        throw new InvalidDataException("Verification failed or performed a transfer: " + verification.Err);
                    var original = await journeyDatabase.LoadRunByIdAsync(target.Run);
                    var retry = await JourneyCli("--cli", "run", "retry", "--run", target.Run, "--pair", rebound.Id,
                        "--choice", "all", "--selection", JsonSerializer.Serialize(new[] { target.Path }), "--yes", "--json");
                    var after = ReadAcceptedReceiptCount(database, rebound.Id);
                    var afterPreview = await journeyHost.LoadRunRetryAsync(target.Run, selectedPairId: rebound.Id);
                    var selected = afterPreview.Items.Single(item => item.Path == target.Path);
                    var exact = after - before == 1 && selected.State == "Completed" && !selected.CanRetry &&
                        (await journeyDatabase.LoadRunByIdAsync(target.Run))!.ResultJson == original!.ResultJson &&
                        expectedBytes.SequenceEqual(await File.ReadAllBytesAsync(source)) &&
                        expectedBytes.SequenceEqual(providerWire.ReadDropboxFixtureBytes(binding,
                            rebound.DropboxRoot.TrimEnd('/') + "/" + target.Path));
                    steps.Add(new { step = "retained_retry", run = target.Run, path = target.Path,
                        retry_exit = retry.Exit, accepted_effect_delta = after - before,
                        original_history_and_local_bytes_preserved = exact, selected_state = selected.State,
                        stderr = retry.Exit == 0 ? null : retry.Err });
                    Console.WriteLine(JsonSerializer.Serialize(steps[^1]));
                    if (!exact) throw new InvalidDataException("Selected retained retry did not complete exactly once: " + retry.Err);
                }
                complete = true;
                steps.Add(new { step = "retained_retry_scope", restored_fixture_files = seeds.Count,
                    selected_retries_complete = true, whole_pair_first_sync_not_claimed = true,
                    reason = "Earlier reviews deliberately skipped other paths; their consent is not expanded by retry." });
                if (completeRetainedReview)
                {
                    // This is NEW explicit fixture consent, not an expansion of any retained retry.
                    // Real business roots are still excluded by the native guards above. Only tiny,
                    // already-owned fixture files and these two fixed fixture inputs are eligible.
                    complete = false;
                    var beforeReview = Sides(database);
                    var originalBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (var file in Directory.GetFiles(isolatedRoot))
                    {
                        RequireSingleLinkFile(file);
                        var name = Path.GetFileName(file);
                        if (name is "journey-final-conflict.txt" or "journey-final-cloud.txt") continue;
                        if (name != "clash.txt" && !name.StartsWith("journey-", StringComparison.Ordinal))
                            throw new InvalidDataException("Fresh fixture consent refuses an unexpected path: " + name);
                        originalBytes.Add(name, await File.ReadAllBytesAsync(file));
                    }
                    if (Directory.GetDirectories(isolatedRoot).Length != 0)
                        throw new InvalidDataException("Fresh fixture consent refuses unexpected child directories.");
                    const string conflictPath = "journey-final-conflict.txt";
                    const string cloudPath = "journey-final-cloud.txt";
                    const string localConflict = "retained clone local conflict input";
                    const string cloudConflict = "retained clone chosen cloud conflict bytes";
                    const string cloudDownload = "retained clone exact cloud-only download bytes";
                    var resumeApprovedReview = (await journeyHost.LoadPairsAsync()).Pairs
                        .Single(pair => pair.Id == rebound.Id).InitialReviewApproved == true;
                    if (File.Exists(Path.Combine(isolatedRoot, conflictPath)))
                    {
                        var current = await File.ReadAllTextAsync(Path.Combine(isolatedRoot, conflictPath));
                        if (current != localConflict && current != cloudConflict)
                            throw new InvalidDataException("The exact retained final-conflict fixture changed.");
                    }
                    else if (resumeApprovedReview)
                        throw new InvalidDataException("Approved fixture lost its resolved conflict file; refuse to recreate it.");
                    else await File.WriteAllTextAsync(Path.Combine(isolatedRoot, conflictPath), localConflict);
                    if (File.Exists(Path.Combine(isolatedRoot, cloudPath)) &&
                        await File.ReadAllTextAsync(Path.Combine(isolatedRoot, cloudPath)) != cloudDownload)
                        throw new InvalidDataException("The exact retained final-download fixture changed.");
                    if (!resumeApprovedReview)
                    {
                        providerWire.AddDropboxFile(binding, rebound.DropboxRoot.TrimEnd('/') + "/" + conflictPath, cloudConflict);
                        providerWire.AddDropboxFile(binding, rebound.DropboxRoot.TrimEnd('/') + "/" + cloudPath, cloudDownload);
                    }
                    var scan = await JourneyCli("--cli", "scan", "--pair", rebound.Id, "--yes", "--json");
                    if (scan.Exit != 0) throw new InvalidDataException("Final fixture inventory failed: " + scan.Err);
                    var total = 0;
                    var expectedTotal = -1;
                    for (var pageIndex = 0; pageIndex < 10; pageIndex++)
                    {
                        var page = await JourneyCli("--cli", "plan", "--pair", rebound.Id, "--limit", "50",
                            "--offset", total.ToString(), "--json");
                        if (page.Exit != 0) throw new InvalidDataException("Final fixture plan failed: " + page.Err);
                        using var document = JsonDocument.Parse(page.Out);
                        var data = document.RootElement.GetProperty("data");
                        expectedTotal = data.GetProperty("totalItems").GetInt32();
                        total += data.GetProperty("items").GetArrayLength();
                        if (!data.GetProperty("hasMore").GetBoolean()) break;
                    }
                    if (total != expectedTotal || total != originalBytes.Count + 2)
                        throw new InvalidDataException("The final fixture review did not cover every exact current item.");
                    if (!resumeApprovedReview)
                    {
                    var apply = await JourneyCli("--cli", "first-sync", "apply", "--pair", rebound.Id,
                        "--allow-recommended", "--yes", "--json");
                    steps.Add(new { step = "complete_review_apply", exit = apply.Exit, output = apply.Out, error = apply.Err });
                    Console.WriteLine(JsonSerializer.Serialize(steps[^1]));
                    if (apply.Exit != 0 && apply.Exit != 5)
                        throw new InvalidDataException("Final fixture apply failed: " + apply.Err);
                    var show = await JourneyCli("--cli", "conflicts", "show", "--pair", rebound.Id,
                        "--path", conflictPath, "--json");
                    if (show.Exit != 0) throw new InvalidDataException("Exact fixture conflict was not exposed: " + show.Err);
                    using var conflictDocument = JsonDocument.Parse(show.Out);
                    var conflict = conflictDocument.RootElement.GetProperty("data").GetProperty("conflict");
                    if (conflict.GetProperty("status").GetString() != "open" ||
                        conflict.GetProperty("localHash").GetString() == conflict.GetProperty("dropboxHash").GetString())
                        throw new InvalidDataException("Fixture conflict must retain both distinct source versions.");
                    var resolve = await JourneyCli("--cli", "conflicts", "resolve", "--pair", rebound.Id,
                        "--path", conflictPath, "--key", conflict.GetProperty("conflictId").GetString()!,
                        "--generation", conflict.GetProperty("generation").GetInt64().ToString(),
                        "--choice", "KeepDropbox", "--yes", "--json");
                    if (resolve.Exit != 0) throw new InvalidDataException("Exact fixture conflict resolution failed: " + resolve.Err);
                    }
                    else
                    {
                        steps.Add(new { step = "resume_approved_review", approved = true,
                            approval_and_conflict_resolution_replayed = false });
                        Console.WriteLine(JsonSerializer.Serialize(steps[^1]));
                    }
                    foreach (var item in originalBytes)
                        if (!item.Value.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(isolatedRoot, item.Key))) ||
                            !item.Value.SequenceEqual(providerWire.ReadDropboxFixtureBytes(binding,
                                rebound.DropboxRoot.TrimEnd('/') + "/" + item.Key)))
                            throw new InvalidDataException("Final fixture did not preserve and upload exact original bytes: " + item.Key);
                    if (await File.ReadAllTextAsync(Path.Combine(isolatedRoot, conflictPath)) != cloudConflict ||
                        await File.ReadAllTextAsync(Path.Combine(isolatedRoot, cloudPath)) != cloudDownload ||
                        Encoding.UTF8.GetString(providerWire.ReadDropboxFixtureBytes(binding,
                            rebound.DropboxRoot.TrimEnd('/') + "/" + conflictPath)) != cloudConflict)
                        throw new InvalidDataException("Final cloud-wins/download bytes differ from the exact fixture source.");
                    var receiptsBeforeRepeat = ReadAcceptedReceiptCount(database, rebound.Id);
                    var repeat = await JourneyCli("--cli", "run", "--pair", rebound.Id, "--yes", "--json");
                    var approved = (await journeyHost.LoadPairsAsync()).Pairs.Single(pair => pair.Id == rebound.Id).InitialReviewApproved;
                    if (repeat.Exit != 0 || approved != true || receiptsBeforeRepeat != ReadAcceptedReceiptCount(database, rebound.Id))
                        throw new InvalidDataException("Final unchanged sync failed, replayed an effect or left first sync unapproved: " + repeat.Err);
                    var afterReview = Sides(database);
                    steps.Add(new { step = "fresh_complete_review", total_items = total, approved,
                        resumed_approved_review = resumeApprovedReview,
                        conflict_choice = "KeepDropbox", exact_file_bytes_verified = total, unchanged_sync_exit = repeat.Exit,
                        original_files_preserved = true, repeated_effects = 0,
                        before = new { beforeReview.Db, beforeReview.Wal, beforeReview.Shm },
                        after = new { afterReview.Db, afterReview.Wal, afterReview.Shm },
                        delta = new { db = afterReview.Db - beforeReview.Db, wal = afterReview.Wal - beforeReview.Wal,
                            shm = afterReview.Shm - beforeReview.Shm } });
                    Console.WriteLine(JsonSerializer.Serialize(steps[^1]));
                    complete = true;
                }
            }
                }
            else
            {
            var missing = new List<string>();
            var prepare = await JourneyCli("--cli", "first-sync", "prepare", "--pair", rebound.Id, "--yes", "--json");
            if (prepare.Exit != 0) missing.Add("prepare exit " + prepare.Exit + ": " + prepare.Err.Trim());
            steps.Add(new { step = "prepare", exit = prepare.Exit, error = prepare.Exit == 0 ? null : prepare.Err.Trim() });
            var recoveryList = await JourneyCli("--cli", "recovery", "list", "--pair", rebound.Id, "--json");
            if (recoveryList.Exit != 0) missing.Add("recovery list exit " + recoveryList.Exit + ": " + recoveryList.Err.Trim());
            steps.Add(new { step = "recovery_list", exit = recoveryList.Exit,
                error = recoveryList.Exit == 0 ? null : recoveryList.Err.Trim() });
            var planPages = 0;
            var planItems = 0;
            var planOffset = 0;
            var planComplete = false;
            for (var pageIndex = 0; pageIndex < 10 && !planComplete; pageIndex++)
            {
                var page = await JourneyCli("--cli", "plan", "--pair", rebound.Id, "--json", "--limit", "50",
                    "--offset", planOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (page.Exit != 0)
                {
                    steps.Add(new { step = "plan", page = pageIndex, exit = page.Exit, error = page.Err.Trim() });
                    missing.Add("plan page " + pageIndex + " exited " + page.Exit);
                    break;
                }
                using var planDocument = JsonDocument.Parse(page.Out);
                var pageItems = planDocument.RootElement.GetProperty("data").GetProperty("items").GetArrayLength();
                planItems += pageItems;
                planPages++;
                planOffset += pageItems;
                if (pageItems < 50) planComplete = true;
            }
            if (!planComplete) missing.Add("plan paging never reached a terminal short page");
            steps.Add(new { step = "plan_pages", pages = planPages, items = planItems, complete = planComplete });
            // Gap 2: two FRESH peer-exclusive local files (the old clash.txt is already reconciled and is not
            // touched). The injected one-shot decline fails exactly one upload; the other must still complete.
            // Option (a): fresh unique fixture names each attempt, so a previously reconciled/conflicted pair of
            // names cannot block the reviewed apply. Existing rows and files are left untouched.
            var runTag = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var okFile = "journey-ok-" + runTag + ".txt";
            var failFile = "journey-fail-" + runTag + ".txt";
            await File.WriteAllTextAsync(Path.Combine(isolatedRoot, okFile), "journey ok payload one");
            await File.WriteAllTextAsync(Path.Combine(isolatedRoot, failFile), "journey fail payload one");
            var scan = await JourneyCli("--cli", "scan", "--pair", rebound.Id, "--yes", "--json");
            if (scan.Exit != 0) missing.Add("scan exit " + scan.Exit + ": " + scan.Err.Trim());
            string? generation = null;
            if (scan.Exit == 0)
            {
                using var scanDocument = JsonDocument.Parse(scan.Out);
                generation = scanDocument.RootElement.GetProperty("data").GetProperty("localScanGenerationId").GetString();
            }
            else { missing.Add("no reviewed scan generation was available"); }
            // Fixture housekeeping: every earlier attempt leaves its own journey-*.txt siblings behind, and the
            // reviewed apply refuses undecided Local-only items. Give each leftover an explicit no-change choice;
            // only the two current files are uploaded.
            var choiceParts = new List<string>
            {
                "\"clash.txt\":\"no-change\"", "\"journey-ok-01.txt\":\"no-change\"",
                "\"journey-fail-01.txt\":\"no-change\"",
                "\"" + okFile + "\":\"upload\"", "\"" + failFile + "\":\"upload\""
            };
            foreach (var leftoverFile in Directory.GetFiles(isolatedRoot, "journey-*.txt"))
            {
                var leftoverName = Path.GetFileName(leftoverFile);
                if (string.Equals(leftoverName, okFile, StringComparison.Ordinal) ||
                    string.Equals(leftoverName, failFile, StringComparison.Ordinal)) continue;
                choiceParts.Add("\"" + leftoverName + "\":\"no-change\"");
            }
            var choices = "{\"__plan\":\"" + (generation ?? string.Empty) + "\"," +
                string.Join(",", choiceParts) + "}";
            var beforeApply = Sides(journeyDatabase.DatabasePath);
            var runIdsBeforeApply = new HashSet<string>(StringComparer.Ordinal);
            using (var preConnection = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False"))
            {
                preConnection.Open();
                using var pre = preConnection.CreateCommand();
                pre.CommandText = "SELECT command_request_id FROM command_request WHERE pair_id=$pair;";
                pre.Parameters.AddWithValue("$pair", rebound.Id);
                using var preReader = pre.ExecuteReader();
                while (preReader.Read())
                    if (!preReader.IsDBNull(0)) runIdsBeforeApply.Add(preReader.GetString(0));
            }
            providerWire.DeclineNextUploadForPath = failFile;
            var apply = await JourneyCli("--cli", "first-sync", "apply", "--pair", rebound.Id, "--yes", "--json",
                "--choices", choices);
            // The decline is an apply-only one-shot fault: guarantee it is disarmed before run verify/run retry.
            providerWire.DeclineNextUploadForPath = null;
            // Admission-boundary capture: the run this apply admitted is the newest retained command_request row
            // for the pair that did not exist before the apply. Deterministic and independent of command kind.
            var runIdsAfterApply = new HashSet<string>(StringComparer.Ordinal);
            using (var postConnection = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False"))
            {
                postConnection.Open();
                using var post = postConnection.CreateCommand();
                post.CommandText = "SELECT command_request_id FROM command_request WHERE pair_id=$pair;";
                post.Parameters.AddWithValue("$pair", rebound.Id);
                using var postReader = post.ExecuteReader();
                while (postReader.Read())
                    if (!postReader.IsDBNull(0)) runIdsAfterApply.Add(postReader.GetString(0));
            }
            var newRunIds = runIdsAfterApply.Except(runIdsBeforeApply, StringComparer.Ordinal).ToArray();
            string? admittedRunId = newRunIds.Length == 1 ? newRunIds[0] : null;
            var admissionValidated = false;
            if (admittedRunId is not null)
            {
                using var admissionConnection = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False");
                admissionConnection.Open();
                using var admission = admissionConnection.CreateCommand();
                admission.CommandText = "SELECT payload_json FROM command_request WHERE command_request_id=$id;";
                admission.Parameters.AddWithValue("$id", admittedRunId);
                var admissionPayload = Convert.ToString(admission.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (admissionPayload.Length > 0)
                {
                    using var admissionDocument = JsonDocument.Parse(admissionPayload);
                    var admissionRoot = admissionDocument.RootElement;
                    var admissionTrigger = admissionRoot.TryGetProperty("trigger", out var triggerValue)
                        ? triggerValue.GetString() : null;
                    var reviewedPlanText = admissionRoot.TryGetProperty("reviewedPlan", out var planValue)
                        ? planValue.GetRawText() : string.Empty;
                    admissionValidated = string.Equals(admissionTrigger, "Reviewed first sync", StringComparison.Ordinal) &&
                        reviewedPlanText.Contains(rebound.Id, StringComparison.Ordinal) &&
                        reviewedPlanText.Contains(okFile, StringComparison.Ordinal) &&
                        reviewedPlanText.Contains(failFile, StringComparison.Ordinal) &&
                        reviewedPlanText.Contains("Upload", StringComparison.Ordinal) &&
                        (generation is null || reviewedPlanText.Contains(generation, StringComparison.Ordinal));
                }
            }
            steps.Add(new { step = "admission_capture", new_run_ids = newRunIds, admitted = admittedRunId,
                validated = admissionValidated });
            if (admittedRunId is null || !admissionValidated)
            {
                admittedRunId = null;
                missing.Add("admission derivation refused: " + newRunIds.Length +
                    " new command(s), structured validation=" + admissionValidated + " for pair " + rebound.Id +
                    " - missing or ambiguous reviewed first-sync admission; dependent effects not started");
            }
            var afterApply = Sides(journeyDatabase.DatabasePath);
            string? originRunId = null;
            if (apply.Out.Trim().Length > 0)
            {
                try
                {
                    using var applyDocument = JsonDocument.Parse(apply.Out);
                    if (applyDocument.RootElement.TryGetProperty("data", out var applyData) &&
                        applyData.ValueKind == JsonValueKind.Object && applyData.TryGetProperty("runId", out var runIdValue))
                        originRunId = runIdValue.GetString();
                }
                catch (Exception) { }
            }
            // The ONLY origin is the unique new reviewed first-sync admission validated by its structured
            // reviewedPlan. No latest-row fallback and no operation-history derivation remain.
            if (admittedRunId is not null) originRunId = admittedRunId;
            steps.Add(new { step = "first_sync_apply", exit = apply.Exit, run_id = originRunId,
                declined_file = failFile, error = apply.Exit == 0 ? null : apply.Err.Trim(),
                db_before_effect = new { beforeApply.Db, beforeApply.Wal, beforeApply.Shm },
                db_after_effect_open = new { afterApply.Db, afterApply.Wal, afterApply.Shm },
                delta_effect = new { db = afterApply.Db - beforeApply.Db, wal = afterApply.Wal - beforeApply.Wal,
                    shm = afterApply.Shm - beforeApply.Shm } });
            if (apply.Exit == 0) missing.Add("the reviewed apply did not surface the injected single-file failure");
            if (originRunId is null) missing.Add("the reviewed apply retained no run identity to retry");
            var receiptsBeforeVerify = ReadAcceptedReceiptCount(database, rebound.Id);
            var retry = originRunId is null
                ? (Exit: -1, Out: string.Empty, Err: "no origin run identity")
                : await JourneyCli("--cli", "run", "verify", "--run", originRunId, "--yes", "--json");
            steps.Add(new { step = "run_verify_uncertain_outcome", exit = retry.Exit,
                error = retry.Exit == 0 ? null : retry.Err.Trim() });
            if (retry.Exit != 0) missing.Add("run verify exit " + retry.Exit + ": " + retry.Err.Trim());
            var receiptsAfterVerify = ReadAcceptedReceiptCount(database, rebound.Id);
            steps.Add(new { step = "verify_write_boundary", before = receiptsBeforeVerify, after = receiptsAfterVerify,
                delta = receiptsAfterVerify - receiptsBeforeVerify });
            if (retry.Exit == 0 && receiptsAfterVerify != receiptsBeforeVerify)
                missing.Add("verify added " + (receiptsAfterVerify - receiptsBeforeVerify) +
                    " accepted business write(s); verification-only must add ZERO");
            // A verification failure must PREVENT the retry, not merely be recorded.
            var verifySucceeded = retry.Exit == 0;
            if (originRunId is not null && verifySucceeded)
            {
                // Gate the retry on the REAL retry-plan for the bound admission: the intended failed path must be
                // offered and the completed sibling must NOT be. Then retry only that path via explicit --selection.
                var retryPlan = await JourneyCli("--cli", "run", "retry-plan", "--run", originRunId,
                    "--pair", rebound.Id, "--json");
                // Parse the retry-plan JSON by its own State/CanRetry fields - list membership is not eligibility.
                var planFailedEligible = false;
                var planSiblingEligible = false;
                var planFailedState = string.Empty;
                if (retryPlan.Exit == 0 && retryPlan.Out.Trim().Length > 0)
                {
                    using var planDocument = JsonDocument.Parse(retryPlan.Out);
                    if (planDocument.RootElement.TryGetProperty("data", out var planData) &&
                        planData.ValueKind == JsonValueKind.Object &&
                        planData.TryGetProperty("items", out var retryPlanItems) &&
                        retryPlanItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var planItem in retryPlanItems.EnumerateArray())
                        {
                            var itemPath = planItem.TryGetProperty("path", out var pathValue)
                                ? pathValue.GetString()
                                : planItem.TryGetProperty("relativePath", out var relativeValue)
                                    ? relativeValue.GetString() : null;
                            var itemCanRetry = planItem.TryGetProperty("canRetry", out var retryValue) &&
                                retryValue.ValueKind == JsonValueKind.True;
                            var itemState = planItem.TryGetProperty("state", out var stateValue)
                                ? stateValue.GetString() : null;
                            if (string.Equals(itemPath, failFile, StringComparison.Ordinal))
                            {
                                planFailedEligible = itemCanRetry;
                                planFailedState = itemState ?? string.Empty;
                            }
                            if (string.Equals(itemPath, okFile, StringComparison.Ordinal)) planSiblingEligible = itemCanRetry;
                        }
                    }
                }
                steps.Add(new { step = "retry_plan", exit = retryPlan.Exit,
                    failed_path_can_retry = planFailedEligible, completed_sibling_can_retry = planSiblingEligible,
                    failed_state = planFailedState, error = retryPlan.Exit == 0 ? null : retryPlan.Err.Trim() });
                if (retryPlan.Exit != 0) missing.Add("run retry-plan exit " + retryPlan.Exit + ": " + retryPlan.Err.Trim());
                if (!planFailedEligible) missing.Add("the retry plan does not offer the failed path as retryable " + failFile);
                // The plan's canRetry flag means 'selectable', not 'retryable-failed' - the completed sibling is
                // still listed. Retransfer prevention is the --selection to the failed path plus the counter delta.
                // The real retry-plan legitimately still lists the completed sibling as already-completed /
                // not retryable - its full account of the run, not an offer to retransfer. Retransfer is prevented
                // by --selection carrying only the failed path and is proven by the accepted-write counter delta.
                var receiptsBeforeRetry = ReadAcceptedReceiptCount(database, rebound.Id);
                // A declined-before-effect upload is a known-no-effect item, not a failed one, so select the choice
                // its own State implies rather than assuming 'failed'.
                var failedChoiceName = planFailedState.Contains("Fail", StringComparison.OrdinalIgnoreCase)
                    ? "failed" : "unattempted";
                retry = planFailedEligible
                    ? await JourneyCli("--cli", "run", "retry", "--run", originRunId, "--pair", rebound.Id,
                        "--choice", failedChoiceName, "--selection", "[\"" + failFile + "\"]", "--yes", "--json")
                    : (Exit: -1, Out: string.Empty, Err: "retry gated: verification/plan prerequisites not satisfied");
                var receiptsAfterRetry = ReadAcceptedReceiptCount(database, rebound.Id);
                steps.Add(new { step = "accepted_write_counters", before_retry = receiptsBeforeRetry,
                    after_retry = receiptsAfterRetry, delta = receiptsAfterRetry - receiptsBeforeRetry });
                if (retry.Exit == 0 && receiptsAfterRetry - receiptsBeforeRetry != 1)
                    missing.Add("accepted-write counter delta was " + (receiptsAfterRetry - receiptsBeforeRetry) +
                        " (expected exactly one accepted effect for " + failFile + " and none for the sibling)");
            }
            var retryComplete = false;
            if (retry.Exit == 0 && retry.Out.Trim().Length > 0)
            {
                using var retryDocument = JsonDocument.Parse(retry.Out);
                retryComplete = retryDocument.RootElement.GetProperty("data").TryGetProperty("firstSyncComplete", out var completeValue) &&
                    completeValue.GetBoolean();
            }
            var retryRun = (await journeyDatabase.LoadRunHistoryAsync(40)).FirstOrDefault(item =>
                originRunId is not null && item.RequestJson.Contains("\"retryOfRunId\":\"" + originRunId + "\"", StringComparison.Ordinal));
            var retryMentionsFail = retryRun?.ResultJson?.Contains(failFile, StringComparison.Ordinal) ?? false;
            var retryMentionsOk = retryRun?.ResultJson?.Contains(okFile, StringComparison.Ordinal) ?? false;
            steps.Add(new { step = "run_retry_failed", exit = retry.Exit, first_sync_complete = retryComplete,
                retained_retry_run = retryRun?.Id, retry_touched_failed_file = retryMentionsFail,
                retry_touched_completed_file = retryMentionsOk, error = retry.Exit == 0 ? null : retry.Err.Trim() });
            if (retry.Exit != 0) missing.Add("run retry exit " + retry.Exit + ": " + retry.Err.Trim());
            if (!retryComplete) missing.Add("the retry did not reach a completed first sync");
            if (retryRun is null) missing.Add("no retained retry run links to the origin run");
            if (!retryMentionsFail) missing.Add("the retry run does not retain the previously failed file");
            if (retryMentionsOk) missing.Add("the retry run re-touched the previously completed file (retransfer risk)");
            complete = missing.Count == 0;
            if (!complete) firstFailure = string.Join(" | ", missing);
            }
            journeyHost.Dispose();
            journeyHttp.Dispose();
            stateLock.Dispose();
            isolatedLease.Dispose();
            connection.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            finalSides = Sides(journeyDatabase.DatabasePath);
            settled = true;
        }
    }
    catch (Exception journeyError)
    {
        firstFailure = journeyError.GetType().Name + ": " + journeyError.Message + "\n" + journeyError.StackTrace;
    }
    if (!settled)
    {
        journeyHost.Dispose();
        journeyHttp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        finalSides = Sides(journeyDatabase.DatabasePath);
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schema = "r697-cloned-state-journey/3",
        clone_root = full,
        host_pairs = verdicts,
        all_reachable_local_roots_inside_owned_allocation = rootsOwned,
        provider_requests_served = providerWire.TotalRequests,
        steps,
        complete,
        first_failure = firstFailure,
        db_after_clean_disposal = new { finalSides.Db, finalSides.Wal, finalSides.Shm }
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(complete
        ? mixedOverlap ? "COMPLETE mixed retained clone: opposite guarded winners, overlapping fixture child retained, exact byte readback, Recovery and unchanged repeat; synthetic provider only."
          : completeRetainedReview ? "COMPLETE retained clone: failed-subset retries, fresh full first-sync approval, cloud conflict choice, exact byte readback and unchanged repeat; synthetic provider only."
          : resumeRetained ? "COMPLETE exact retained retry proof; whole-pair first-sync approval remains separately scoped."
          : "COMPLETE cloned-state journey: the retained pair was rebound onto an owned isolated root and the " +
          "prepare/plan/conflict/apply/readback legs ran through the real product with in-process provider replies"
        : "PARTIAL cloned-state journey: " + (firstFailure ?? "an effect leg did not complete") +
          " (pair rows " + pairRows + ", published pairs " + journeyPairs.Count + ")");
    return complete ? 0 : 3;
}

if (args is ["--verified-retry-checks", var verifiedRetryRoot])
{
    await FirstSyncEffectChecks.RunVerifiedRetryAsync(Path.GetFullPath(verifiedRetryRoot));
    return 0;
}

if (args is ["--first-sync-scaling-benchmark", var scalingRoot, .. var scalingRest])
{
    var scalingState = scalingRest.Length > 0 && !scalingRest[0].All(char.IsDigit) ? scalingRest[0] : scalingRoot;
    var scalingSizes = scalingRest.Where(item => item.All(char.IsDigit)).Select(int.Parse).ToArray();
    await FirstSyncScalingBenchmark.RunAsync(Path.GetFullPath(scalingRoot), Path.GetFullPath(scalingState),
        scalingSizes.Length == 0 ? [1_000, 5_000, 20_000] : scalingSizes);
    return 0;
}

if (args is ["--retained-inventory-profile", var inventoryProfileSource, var inventoryProfileRoot, var inventoryProfilePair])
{
    await ObservationIdentityProgressChecks.ProfileRetainedAsync(Path.GetFullPath(inventoryProfileSource),
        Path.GetFullPath(inventoryProfileRoot), inventoryProfilePair);
    return 0;
}

if (args is ["--first-sync-effect-checks", var firstSyncRoot])
{
    // Also exercise the real shared full/paged query and its input/root fences.
    await BootstrapPagingAndGrouping();
    await BootstrapReviewedDirectionFence();
    await FirstSyncEffectChecks.RunAsync(Path.GetFullPath(firstSyncRoot));
    return 0;
}

if (args is ["--pair-editor-checks", var pairEditorRoot])
{
    await PairEditorChecks.RunAsync(Path.GetFullPath(pairEditorRoot));
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "--hard-link-probe", StringComparison.Ordinal))
{
    try
    {
        NativeTestFiles.CreateHardLink(args[1], args[2]);
        Environment.ExitCode = 0;
    }
    catch (IOException)
    {
        Environment.ExitCode = 5;
    }
    return Environment.ExitCode;
}

if (args.Length == 4 && string.Equals(args[0], "--acl-crash-probe", StringComparison.Ordinal))
{
    var mode = args[1];
    var root = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(root);
    var objectPath = CrashObjectPath(root, mode);
    Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
    var authority = SyncLocalFileAuthority.CreateNewFile(objectPath);
    var writer = authority.CreateGuardedWriteStream();
    writer.Write(CrashPayload());
    writer.Flush();
    authority.Stream.Flush(true);
    if (string.Equals(mode, "promoted", StringComparison.Ordinal))
    {
        using var rootLease = PilotDirectoryLease.Open(root);
        authority.RenameTo(rootLease, CrashTargetName());
    }
    NativeTestFiles.TerminateCurrentProcess(197);
    return 197;
}

if (args.Length == 6 && string.Equals(args[0], "--cleanup-crash-probe", StringComparison.Ordinal))
{
    await RunCleanupCrashProbeAsync(args[1], args[2], bool.Parse(args[3]), Path.GetFullPath(args[4]));
    return 198;
}

if (args.Length == 4 && string.Equals(args[0], "--true-sync-crash-probe", StringComparison.Ordinal))
{
    await RunTrueSyncCrashProbeAsync(args[1], Path.GetFullPath(args[2]));
    return 199;
}

if (args.Length == 3 && string.Equals(args[0], "--cloud-pro-wal-crash-probe", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunWalCrashProbeAsync(Path.GetFullPath(args[1]));
    return 203;
}

if (args.Length == 4 && string.Equals(args[0], "--authority-fresh-create-crash-probe", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunFreshCreateCrashProbeAsync(args[1], Path.GetFullPath(args[2]));
    return 211;
}

if (args.Length == 5 && string.Equals(args[0], "--control-allocation-crash-probe", StringComparison.Ordinal))
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("The control allocation crash probe requires Windows NTFS.");
    await CloudProAuthorityPreflight.RunControlAllocationCrashProbeAsync(args[1], Path.GetFullPath(args[2]),
        Path.GetFullPath(args[3]));
    return 207;
}

if (args.Length == 3 && string.Equals(args[0], "--recovery-spool-crash-probe", StringComparison.Ordinal))
{
    var spoolRoot = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(spoolRoot);
    DropboxPilotClient.RecoverySpoolRootForTest = spoolRoot;
    SyncRecoverySpoolAdmission.ResetForTest();
    var bytes = new byte[1024 * 1024];
    for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
    await using var source = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(source);
    SyncRecoverySpoolAdmission.CapacityBytesForTest = bytes.LongLength;
    var handler = new DropboxRecoverySpoolHandler(bytes, hash, "process-crash-during-upload", spoolRoot);
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder");
    _ = await client.CopyToRecoveryAsync(new PilotRemoteEntry("large.bin", PilotEntryKind.File,
            "id:large-source", "rev:large-source", bytes.LongLength, hash),
        "/Apps/Sync Shark/Recovery/pair-identifier/4123456789abcdef0123456789abcdef");
    throw new InvalidDataException("The recovery-spool crash probe returned past forced termination.");
}

if (args.Length == 4 && string.Equals(args[0], "--cross-pair-generation-crash-probe",
        StringComparison.Ordinal))
{
    await RunCrossPairGenerationCrashProbeAsync(args[1], Path.GetFullPath(args[2]));
    return 214;
}

if (args.Length == 4 && string.Equals(args[0], "--cross-pair-directory-effect-crash-probe",
        StringComparison.Ordinal))
{
    await RunCrossPairDirectoryEffectCrashProbeAsync(args[1], Path.GetFullPath(args[2]));
    return 215;
}

if (args.Length == 1 && string.Equals(args[0], "--safe-sync-preflight", StringComparison.Ordinal))
{
    await TrueSyncCapabilityPreflight();
    Console.WriteLine("PASS true-sync capability preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--pair-selection-default-contract", StringComparison.Ordinal))
{
    await PairSelectionDefaultContract();
    Console.WriteLine("PASS pair selection default, legacy compatibility, traversal, and persistence contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--folder-hierarchy-degradation-contract", StringComparison.Ordinal))
{
    await FolderHierarchyDegradationContract();
    Console.WriteLine("PASS one unavailable endpoint does not blank the healthy folder hierarchy");
    return 0;
}

if (args is ["--bootstrap-move-review-checks", var moveReviewRoot])
{
    await BootstrapMoveReviewChecks.RunAsync(Path.GetFullPath(moveReviewRoot));
    return 0;
}

if (args is ["--bootstrap-baseline-review-checks", var baselineReviewRoot])
{
    await BootstrapMoveReviewChecks.RunBaselineUpdatesAsync(Path.GetFullPath(baselineReviewRoot));
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--endpoint-hierarchy-routing-checks", StringComparison.Ordinal))
{
    return await EndpointHierarchyRoutingChecks.RunAsync(Path.GetFullPath(args[1]));
}

if (args.Length == 1 && string.Equals(args[0], "--pair-lifecycle-ui-contract", StringComparison.Ordinal))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await PairLifecycleUiContract();
    Console.WriteLine("PASS pair retention compatibility, removal, reselection, and authoritative retirement contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--dropbox-browser-contract", StringComparison.Ordinal))
{
    await DropboxFolderBrowserContract();
    await DropboxFolderBrowserPagination();
    Console.WriteLine("PASS Dropbox folder-browser live-shape, bounded pagination, and cursor-cycle contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--dropbox-root-echo-contract", StringComparison.Ordinal))
{
    await DropboxRecursiveRootEchoContract();
    await DropboxMountedInventoryContract();
    Console.WriteLine("PASS Dropbox recursive root-echo contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--discovery-correction-preflight", StringComparison.Ordinal))
{
    await SafeExitCheckpointContract();
    CorrectionWitness("C02", "safe-exit-checkpoint-timeout-and-restart");
    await DropboxRecoverySpoolContract();
    await DropboxControlNamespaceMarkerWireContract();
    CorrectionWitness("C03", "shared-volume-recovery-keep-both-admission-crash-cleanup-and-membership");
    await DropboxRewindRollbackContract();
    CorrectionWitness("C04", "rewind-state-restart-stop-drift-conflict-and-resume");
    await LocalMonitorAndUsnContract();
    CorrectionWitness("C05", "durable-before-discard-bounded-monitor-overflow-dirty-rescan-restart");
    await PairFailureIsolationContract();
    CorrectionWitness("C06", "single-run-all-and-startup-pair-isolation");
    await DropboxRangeDownloadWireContract();
    await WindowsCloudFilesContract();
    CorrectionWitness("C07", "verified-small-range-and-cloud-files-hydration");
    await MutableStateLifetimeAndMigration();
    CorrectionWitness("C09", "mutable-state-compaction-migration-and-torn-write-recovery");
    await CloudProAuthorityPreflight.RunAsync();
    CorrectionWitness("C13", "ancestor-uncertainty-subtree-fence-across-restart");
    CorrectionWitness("C14", "ancestor-type-conflict-subtree-fence-across-restart");
    CorrectionWitness("C15", "source-root-membership-before-after-and-streamed-copy");
    await DropboxFolderBrowserContract();
    await DropboxFolderBrowserPagination();
    CorrectionWitness("C16", "bounded-streaming-pagination-adverse-and-create-readback");
    Console.WriteLine("PASS executed discovery-correction core preflight (11 correction classes)");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--rev3-monitor-correction-preflight", StringComparison.Ordinal))
{
    await LocalMonitorAndUsnContract();
    Console.WriteLine("PASS Rev3 monitor admission, failure, restart, rescan, and convergence contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--rev3-spool-correction-preflight", StringComparison.Ordinal))
{
    await DropboxRecoverySpoolContract();
    Console.WriteLine("PASS Rev3 shared recovery-spool admission, cancellation, release, and progress contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--rev3-pair-isolation-preflight", StringComparison.Ordinal))
{
    await PairFailureIsolationContract();
    Console.WriteLine("PASS Rev3 operation-scoped same-account pair-isolation and recovery contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--dropbox-download-contract", StringComparison.Ordinal))
{
    await RecoveryDownloadCorrection.RunMetadataAsync();
    await RecoveryDownloadCorrection.RunMarkerAsync();
    await DropboxDownloadWireContract();
    await DropboxRangeDownloadWireContract();
    await DropboxArchiveWireContract();
    await DropboxRecoverySpoolContract();
    await DropboxControlNamespaceMarkerWireContract();
    Console.WriteLine("PASS Dropbox revision-path download contract");
    return 0;
}

if (args is ["--local-create-receipt-recovery-checks", var localReceiptRecoveryRoot])
{
    await CloudProAuthorityPreflight.RunLocalCreateReceiptRecoveryAsync(Path.GetFullPath(localReceiptRecoveryRoot));
    return 0;
}

if (args is ["--local-create-preparation-recovery-checks", var localPreparationRecoveryRoot])
{
    await CloudProAuthorityPreflight.RunLocalCreatePreparationRecoveryAsync(Path.GetFullPath(localPreparationRecoveryRoot));
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--recovery-copy-correction", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunRecoveryCorrectionAsync();
    return 0;
}

if (args.Length is 1 or 2 && string.Equals(args[0], "--recovery-consumer-correction", StringComparison.Ordinal))
{
    await RecoveryConsumerCorrection.RunAsync(args.Length == 2 ? args[1] : "all");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--nested-local-update-path", StringComparison.Ordinal))
{
    await RecoveryConsumerCorrection.RunNestedLocalPathCorrectionAsync();
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--recovery-hydration-correction", StringComparison.Ordinal))
{
    await RecoveryDownloadCorrection.RunNativeRangeAsync();
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--upload-session-contract", StringComparison.Ordinal))
{
    await DropboxUploadSessionOffsetRecovery();
    await DropboxUploadSessionExpiryRecovery();
    await DropboxUploadSessionFinishUncertain();
    Console.WriteLine("PASS Dropbox persistent upload-session resume contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--true-sync-preflight", StringComparison.Ordinal))
{
    await TrueSyncCapabilityPreflight();
    await CreateNewCollisionPreserved();
    Console.WriteLine("PASS true-sync capability preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--true-sync-crash-preflight", StringComparison.Ordinal))
{
    await TrueSyncProcessCrashRecovery();
    Console.WriteLine("PASS true-sync crash transition preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--true-sync-functional-preflight", StringComparison.Ordinal))
{
    await RecoverableDeletionPropagation();
    await RecoverableRenamePropagation();
    await RecoverableDeletionArchiveUncertain();
    await RecoverySourceAbsenceIsNotProof();
    await FixedLengthRecoveryName();
    await RecoverableConflictResolution();
    await LocalRecoveryResponseLoss();
    await LocalRecoveryIdentityMismatch();
    await LocalRecoveryCollisionPreserved();
    await ConflictTargetIdentityMismatch();
    await SameByteLocalIdentityReplacementConflicts();
    await CompositeConflictRecovery();
    Console.WriteLine("PASS true-sync deletion and conflict functional preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--broad-failure-correction-preflight", StringComparison.Ordinal))
{
    await RecoveryWriteHardLinkIsolation();
    await CleanupPendingCrashMatrix();
    await CleanupFailureMatrix();
    Console.WriteLine("PASS broad-failure correction preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--recovery-registry-preflight", StringComparison.Ordinal))
{
    await WritableObjectCrashRecovery();
    await NearCeilingCrashRecovery();
    await CleanupArtifactIdentityMismatch();
    Console.WriteLine("PASS registered writable-object, near-ceiling, and cleanup-identity recovery witnesses");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--reviewer-2-rejection-preflight", StringComparison.Ordinal))
{
    await Reviewer2RejectionPreflight();
    Console.WriteLine("PASS 24/24 reviewer-successor safety assertions");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--master-selected-startup-preflight", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunStrictStartupPreflightAsync();
    await Reviewer2RejectionPreflight();
    Console.WriteLine("PASS strict fresh-create/current-schema startup and preserved true-sync source boundaries");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--download-recovery-preflight", StringComparison.Ordinal))
{
    await BidirectionalDownloadRecovery();
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cloud-pro-authority-preflight", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunAsync();
    Console.WriteLine("PASS Cloud Pro SQLite authority preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--ui-reliability-delta-contract", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunUiReliabilityDeltaAsync();
    await BootstrapRuntimeActionPlanContract();
    await CloudProAuthorityPreflight.RunCrossPairLiveActionDeltaAsync();
    Console.WriteLine("PASS live action stream and retained Activity projection contract");
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "--true-sync-contract-preflight", StringComparison.Ordinal))
{
    await TrueSyncContractPreflight.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
        Path.GetFullPath(args[3]));
    Console.WriteLine("PASS reviewed true-sync contract inventory preflight");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-move-contract", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunCrossPairMovePreflightAsync();
    Console.WriteLine("PASS identity-preserving cross-pair movement contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-generation-reproduction", StringComparison.Ordinal))
{
    await CrossPairGenerationReproductionContract();
    Console.WriteLine("PASS SS-917E-F01 control-disabled comparison and coordinated correction");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-directory-descendant-reproduction",
        StringComparison.Ordinal))
{
    await CrossPairDirectoryDescendantRuntimeContract();
    Console.WriteLine("PASS cross-pair directory descendant full-runtime contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-directory-effect-crash-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairDirectoryProcessCrashAfterEffectContract);
    Console.WriteLine("PASS child-process populated-directory effect crash and recovery contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-directory-adverse-ordering-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairDirectoryStaleDescendantBeforeRootContract);
    Console.WriteLine("PASS populated-directory stale-descendant-before-root contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-directory-collision-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairDirectoryCollisionContract);
    Console.WriteLine("PASS populated-directory destination collision contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-directory-divergence-contract",
        StringComparison.Ordinal))
{
    await CrossPairDirectoryDescendantRuntimeContract();
    await WithTempRoot(CrossPairDirectoryStaleDescendantBeforeRootContract);
    await WithTempRoot(CrossPairDirectoryPrefixAuthorityContract);
    await WithTempRoot(CrossPairDirectoryDivergentDescendantContract);
    Console.WriteLine("PASS populated-directory legitimate success, subtree-prefix authority, adverse ordering, closed-set divergence, and restart contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--planning-invalidation-contract", StringComparison.Ordinal))
{
    await CrossPairGenerationReproductionContract(onlyInvalidation: true);
    Console.WriteLine("PASS coordinated planning invalidation contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-ready-authority-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairReadyAuthorityTransitionContract);
    Console.WriteLine("PASS cross-pair Ready authority-transition contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--ordinary-ready-invalidation-matrix-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(OrdinaryReadyOperationInvalidationMatrixContract);
    Console.WriteLine("PASS ordinary Ready create/delete/move invalidation matrix contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--selective-exclusion-failure-isolation-contract",
        StringComparison.Ordinal))
{
    await WithTempRoot(SelectiveExclusionFailureIsolationContract);
    Console.WriteLine("PASS selective-exclusion peer-failure isolation contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--pair-failure-isolation-contract",
        StringComparison.Ordinal))
{
    await PairFailureIsolationContract();
    Console.WriteLine("PASS pair and membership failure-isolation contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--coordinated-lifecycle-contract", StringComparison.Ordinal))
{
    await WithTempRoot(async root =>
    {
        await CrossPairRestartBeforeDispatchContract(root);
        await CrossPairPauseBeforeEffectContract(root);
        await CoordinatedPauseBeforeEffectContract(root);
        await CloudProAuthorityPreflight.RunDeletionOutcomeRetentionChecksAsync(root);
        await CoordinatedStopDrainChecks.RunAsync(root);
    });
    Console.WriteLine("PASS coordinated stop, restart, and pause lifecycle contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-monitor-entry-contract", StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairMonitorEntryPointContract);
    Console.WriteLine("PASS actual debounced monitor callback cross-pair contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--concurrent-cross-pair-contract", StringComparison.Ordinal))
{
    await WithTempRoot(ConcurrentCrossPairMovesContract);
    Console.WriteLine("PASS concurrent independent cross-pair move contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cross-pair-process-crash-contract", StringComparison.Ordinal))
{
    await WithTempRoot(CrossPairProcessCrashBeforeDispatchContract);
    Console.WriteLine("PASS child-process cross-pair planning crash and restart contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--coordinated-pause-before-effect-contract", StringComparison.Ordinal))
{
    await WithTempRoot(CoordinatedPauseBeforeEffectContract);
    Console.WriteLine("PASS coordinated pause freshness contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--authority-migration-contract", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunVersion2MigrationPreflightAsync();
    Console.WriteLine("PASS v0.2.4 authority migration and rollback contract");
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--installed-state-migration-contract", StringComparison.Ordinal))
{
    await CloudProAuthorityPreflight.RunInstalledStateMigrationPreflightAsync(
        Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    Console.WriteLine("PASS exact installed v0.2.4 state migration, interruption, and rollback contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--selective-scale-contract", StringComparison.Ordinal))
{
    await SelectiveSubtreeTraversal();
    await FolderSelectionIndexPaging();
    await BootstrapPagingAndGrouping();
    await ConflictProjectionPagingAndGrouping();
    await OfflineSelectivePolicyContract();
    Console.WriteLine("PASS selective subtree and persistent paged hierarchy and bootstrap contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--first-sync-action-contract", StringComparison.Ordinal))
{
    await BootstrapPagingAndGrouping();
    await BootstrapReviewedDirectionFence();
    await BootstrapRuntimeActionPlanContract();
    Console.WriteLine("PASS first-sync filters, real runtime actions, persisted activity, exclusions, stale review, direction fence, and live action contract");
    return 0;
}

if (args is ["--first-sync-retention-contract", var retentionRoot])
{
    await FirstSyncRetentionChecks.RunAsync(Path.GetFullPath(retentionRoot));
    Console.WriteLine("PASS first-sync retention, terminal outcome, typed problem, refused replay and follow-up failure contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--cloud-files-contract", StringComparison.Ordinal))
{
    await WindowsCloudFilesContract();
    Console.WriteLine("PASS Windows Cloud Files registration, placeholder, hydration, and dehydration contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--availability-identity-correction",
        StringComparison.Ordinal))
{
    await AvailabilityIdentityCorrectionContract();
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--account-connection-contract", StringComparison.Ordinal))
{
    await DropboxConnectionLifecycle();
    await WithTempRoot(DropboxAccountCacheChecks.RunAsync);
    Console.WriteLine("PASS Dropbox account, disconnect, reconnect, and plural-ready connection contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--rewind-contract", StringComparison.Ordinal))
{
    await DropboxRewindRollbackContract();
    await WithTempRoot(CloudPairHostBindingChecks.RunCloudRewindRefusalChecksAsync);
    Console.WriteLine("PASS Dropbox Rewind pause, preview, restart, apply, conflict, and resume contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--performance-contract", StringComparison.Ordinal))
{
    await TransferThroughputAndRateLimitContract();
    await PairConcurrencyContract();
    await PairFailureIsolationContract();
    await LocalMonitorAndUsnContract();
    Console.WriteLine("PASS bandwidth pacing, cancellation, rate-limit retry, and provider-call metrics contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--automation-background-contract", StringComparison.Ordinal))
{
    await AutomationSettings();
    await MigratedPairFirstInventory();
    await LocalMonitorAndUsnContract();
    Console.WriteLine("PASS automatic/manual pair policy, migrated restart, logon-state, and monitor contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--recovery-browser-contract", StringComparison.Ordinal))
{
    await RecommendedRecoveryFolderContract();
    await RecoveryBrowserLocalRestoreContract();
    await RecoveryBrowserDropboxRestoreContract();
    Console.WriteLine("PASS local and Dropbox recovery catalog, pagination, revisions, preview, restore-original, restore-copy, collision, offline, and restart contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--dropbox-artifact-containment-contract",
        StringComparison.Ordinal))
{
    await RecommendedRecoveryFolderContract();
    await DropboxArtifactContainmentContract();
    await PersistedInvalidRecoveryBindingContract();
    await RecoveryConsumerCorrection.RunAsync("containment-lifecycle");
    Console.WriteLine("PASS Sync Shark Dropbox artifact containment contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--edge-contract", StringComparison.Ordinal))
{
    await RecursiveNamespaceFailureContract();
    await DropboxInterruptedListingContract();
    await ProjectionSchemaContract();
    await ConflictSelectionGenerationFence();
    Console.WriteLine("PASS locked path, reparse isolation, invalid name, permission, and interrupted pagination contract");
    return 0;
}

if (args.Length == 1 && string.Equals(args[0], "--scale-performance-contract", StringComparison.Ordinal))
{
    await ManyPairAuthorityContract();
    await FolderSelectionIndexPaging();
    await BootstrapPagingAndGrouping();
    await ConflictProjectionPagingAndGrouping();
    await ReleaseScaleMetricsContract();
    Console.WriteLine("PASS pair-count, inventory responsiveness, memory, and paged-surface release targets");
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Unsupported focused test selector: " + string.Join(" ", args));
    return 2;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Dropbox empty content hash", EmptyContentHash),
    ("Upload-only pilot acceptance", UploadOnlyAcceptance),
    ("Rerun idempotently skips completed uploads", RerunSkipsCompletedUploads),
    ("Nested folders synchronize recursively", NestedFoldersSynchronize),
    ("Local root identity is persisted and revalidated", LocalRootIdentity),
    ("Large local inventories are not silently truncated", LargeLocalInventory),
    ("Large remote inventories are not silently truncated", LargeRemoteInventory),
    ("Selective subtree indexing skips excluded branches and reaches explicit child inclusions", SelectiveSubtreeTraversal),
    ("Persistent folder hierarchy pages and searches without retaining every row in the UI", FolderSelectionIndexPaging),
    ("Complete observations reuse unchanged hashes and rehash only changed files", ChangedOnlyHashing),
    ("Thousands-item first sync review pages, searches, and groups in SQLite", BootstrapPagingAndGrouping),
    ("Reviewed first-sync actions reject a changed direction before effect", BootstrapReviewedDirectionFence),
    ("Conflict groups page and capture an exact unloaded-row selection", ConflictProjectionPagingAndGrouping),
    ("Append-only state repairs torn and corrupt tails with evidence", AppendOnlyState),
    ("State recovery retains structural record and byte reserve", RecoveryCeilingReserve),
    ("Alternating state retains a valid predecessor across a torn slot", DualSlotRecovery),
    ("Frequently mutated settings migrate once and remain writable beyond legacy JSONL ceilings", MutableStateLifetimeAndMigration),
    ("Instance lock is cross-process-file exclusive", InstanceLockExclusive),
    ("State file identity substitution is rejected", StateIdentitySubstitution),
    ("Mutable state hard-link aliases are rejected before mutation", MutableStateHardLinks),
    ("Dropbox upload uses add string and folder ID child path", DropboxUploadWireContract),
    ("Dropbox upload HTTP failure after dispatch is effect-uncertain", DropboxUploadHttpFailureUncertain),
    ("Dropbox update uses the exact expected revision", DropboxUpdateWireContract),
    ("Dropbox upload session resumes from the provider-confirmed offset", DropboxUploadSessionOffsetRecovery),
    ("Dropbox expired upload session restarts without a visible duplicate", DropboxUploadSessionExpiryRecovery),
    ("Dropbox lost finish response remains reconciliation-gated", DropboxUploadSessionFinishUncertain),
    ("Dropbox download revalidates metadata and content hash", DropboxDownloadWireContract),
    ("Dropbox range download transfers only the requested identity-bound bytes", DropboxRangeDownloadWireContract),
    ("Dropbox deletion propagation archives the exact revision", DropboxArchiveWireContract),
    ("Large Dropbox recovery and keep-both copies use a bounded disk spool", DropboxRecoverySpoolContract),
    ("Dropbox recovery ownership marker downloads by exact out-of-pair path and revision", DropboxControlNamespaceMarkerWireContract),
    ("Empty Dropbox folder binding captures stable ID", EmptyFolderBinding),
    ("Dropbox folder browser lists, continues, and creates by stable identity", DropboxFolderBrowserContract),
    ("Dropbox folder browser exhausts every unique cursor", DropboxFolderBrowserPagination),
    ("Recommended Dropbox Recovery is stable, idempotent, and protected from pair selection", RecommendedRecoveryFolderContract),
    ("Sync Shark-generated Dropbox artifacts stay inside the product container", DropboxArtifactContainmentContract),
    ("Nonempty Dropbox folder cannot be first-bound", NonemptyFolderRejected),
    ("Interrupted upload acknowledgement is reported truthfully", UncertainUploadTruthful),
    ("OAuth request is offline PKCE with registered product scopes", OAuthRequest),
    ("OAuth callback binds exact path and state", OAuthCallbackValidation),
    ("OAuth callback acknowledgement is immediate and app-authoritative", OAuthAcknowledgement),
    ("Dropbox connection lifecycle preserves identity and fails closed across accounts", DropboxConnectionLifecycle),
    ("UI theme preference is validated and persisted", UiThemePreference),
    ("Durable bounded sync history survives reload", DurableSyncHistory),
    ("Continuous sync settings are validated and persisted", AutomationSettings),
    ("Product exit waits for a durable run checkpoint and rejects timeout", SafeExitCheckpointContract),
    ("More than sixteen pair definitions survive authoritative reload", ManyPairAuthorityContract),
    ("Migrated v0.2.4 pairs enter effect-free recursive discovery without re-entry", MigratedPairFirstInventory),
    ("Local monitoring and USN gap classification persist across restart", LocalMonitorAndUsnContract),
    ("Paged conflict and path projections match the current authority schema", ProjectionSchemaContract),
    ("Conflict bulk selection rejects a changed generation before effects", ConflictSelectionGenerationFence),
    ("Recovery browser restores protected local and exact Dropbox objects", async () =>
    {
        await RecoveryBrowserLocalRestoreContract();
        await RecoveryBrowserDropboxRestoreContract();
    }),
    ("Local and Dropbox roots are bounded", BoundedRoots),
    ("Legacy pair migrates and multiple pairs persist", MultiplePairPersistence),
    ("Recovery scheduling prioritizes an abandoned active run then oldest pending pair", RecoveryFirstMultiPairOrdering),
    ("Fresh aggregate migration retains the legacy pair and baseline", AggregateMigrationRecovery),
    ("Corrupt aggregate slots refuse recreation without losing retained evidence", AggregateCorruptStateRefusal),
    ("Editing pair identities rebinds the pair and retires its old runtime state", PairIdentityRebinding),
    ("Pair policy saves preserve runtime and active or unknown runs fence configuration", PairPolicyAndRunFence),
    ("Held local mutations affect the opened object despite pathname substitution", HeldLocalObjectMutation),
    ("Initial bidirectional discovery is effect-free", BidirectionalBaseline),
    ("New files transfer once in both directions", BidirectionalNewFiles),
    ("Create-new destination collisions are preserved across the next run", CreateNewCollisionPreserved),
    ("One-sided modifications update only the unchanged peer", BidirectionalModifications),
    ("Two-sided modifications preserve both versions", BidirectionalConflict),
    ("Interrupted upload is resolved by inventory without duplicate effect", BidirectionalUploadRecovery),
    ("Uncertain pre-effect upload is inventoried before one retry", BidirectionalUploadNoEffectRecovery),
    ("Interrupted download is retried from persisted intent", BidirectionalDownloadRecovery),
    ("Concurrent hard-link creation during provider write is rejected", DownloadStageHardLinkIsolation),
    ("Recovery writes reject concurrent hard-link aliases", RecoveryWriteHardLinkIsolation),
    ("Prepared private transactions recover without adopting unknown artifacts", PrivateTransactionPreparedRecovery),
    ("Private transaction identity substitution is rejected", PrivateTransactionIdentityRecovery),
    ("Unbound internal-name lookalikes are counted and preserved", UnboundArtifactLookalikeIsCounted),
    ("Writable objects recover after abrupt process termination", WritableObjectCrashRecovery),
    ("Near-ceiling crash recovery preserves bounded capacity", NearCeilingCrashRecovery),
    ("Committed cleanup survives every crash boundary", CleanupPendingCrashMatrix),
    ("Committed cleanup retries every bounded failure boundary", CleanupFailureMatrix),
    ("Cleanup rejects artifact identity substitution", CleanupArtifactIdentityMismatch),
    ("Deletion propagation archives both local and Dropbox predecessors", RecoverableDeletionPropagation),
    ("Renames converge through recoverable archive and create operations", RecoverableRenamePropagation),
    ("Uncertain Dropbox deletion archive responses recover by inventory", RecoverableDeletionArchiveUncertain),
    ("Missing recovery sources never prove an archive effect", RecoverySourceAbsenceIsNotProof),
    ("Recovery artifact names remain fixed and bounded", FixedLengthRecoveryName),
    ("Both explicit conflict choices preserve the losing version", RecoverableConflictResolution),
    ("Lost local-recovery acknowledgements converge without duplicate effects", LocalRecoveryResponseLoss),
    ("Local recovery identity substitution is rejected", LocalRecoveryIdentityMismatch),
    ("Unknown local recovery destination collisions are preserved", LocalRecoveryCollisionPreserved),
    ("Same-byte conflict target identity substitution is rejected", ConflictTargetIdentityMismatch),
    ("Same-byte local object replacement is treated as a conflict", SameByteLocalIdentityReplacementConflicts),
    ("Composite conflict recovery converges after partial effects", CompositeConflictRecovery),
    ("True-sync risk layer passes replacement, conflict, deletion and restart preflight", TrueSyncCapabilityPreflight),
    ("True-sync replacement survives process termination at commit and cleanup boundaries", TrueSyncProcessCrashRecovery),
    ("An exact-file CLI upload commits once and reports every refusal, no-effect and unknown outcome",
        ExactFileUploadContract),
    ("Other-provider placeholders remain isolated without hydration", OtherProviderPlaceholderIsolation)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine("PASS " + test.Name);
        passed++;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("FAIL " + test.Name + ": " + exception.Message);
    }
}
Console.WriteLine($"{passed}/{tests.Length} checks passed");
return passed == tests.Length ? 0 : 1;

static async Task AvailabilityIdentityCorrectionContract()
{
    if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out _))
        throw new PlatformNotSupportedException(
            "This correction proof requires the Windows Cloud Files platform.");
    await WithTempRoot(async scenarioRoot =>
    {
        var localRoot = Path.Combine(scenarioRoot, "pair");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "toggle.txt", "availability-stable-bytes");
        Write(localRoot, "local-change.txt", "local-baseline");
        Write(localRoot, "remote-change.txt", "remote-baseline");
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        SyncPair pair;
        using (var lease = PilotDirectoryLease.Open(localRoot))
        {
            pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/AvailabilityIdentity", "id:availability-identity-root"),
                "Availability identity", connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                FilesOnDemandEnabled = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:availability-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
        }
        await pairStore.UpsertAsync(pair, select: true);
        var remote = new FakeSyncRemoteStore();
        remote.PutWithId("toggle.txt", "availability-stable-bytes", "id:availability-toggle");
        remote.PutWithId("local-change.txt", "local-baseline", "id:availability-local-change");
        remote.PutWithId("remote-change.txt", "remote-baseline", "id:availability-remote-change");
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
        var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (_, _) => remote);
        try
        {
            var first = await host.RunPairsAsync([pair], "Availability identity baseline");
            var stable = await host.RunPairsAsync([pair], "Availability identity stable baseline");
            True(first.Items.Single().FailureMessage is null && stable.Items.Single().FailureMessage is null);

            Write(localRoot, "automatic-upload.txt", "automatic-online-only-bytes");
            var automaticUpload = await host.RunPairsAsync([pair], "Automatic online-only upload");
            True(automaticUpload.Items.Single().FailureMessage is null &&
                 automaticUpload.Items.Single().Result is { Conflicts: 0, Blocked: 0 });
            True(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                     Path.Combine(localRoot, "automatic-upload.txt"), out var automaticPlaceholder) &&
                 automaticPlaceholder is { State.Availability: SyncCloudFileAvailability.OnlineOnly,
                     State.IsInSync: true });
            var automaticPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "automatic-upload.txt", StringComparison.OrdinalIgnoreCase));
            var automaticVersion = await authority.LoadObjectVersionAsync(automaticPoint.LocalObjectVersionId!);
            using (var automaticIdentity = JsonDocument.Parse(automaticVersion.IdentityJson))
                True(automaticIdentity.RootElement.TryGetProperty("cloudFiles", out _));
            var operationsAfterAutomaticUpload = await CountRowsAsync(authority.DatabasePath, "operation");
            var recoveryAfterAutomaticUpload = (await authority.LoadRecoveryArtifactsAsync()).Count;
            var effectsAfterAutomaticUpload = (remote.UploadCount, remote.ArchiveCount, remote.VisibleCopyCount,
                remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount);
            var automaticFreshObservation = await host.RunPairsAsync([pair],
                "Fresh observation after automatic online-only upload");
            True(automaticFreshObservation.Items.Single().FailureMessage is null &&
                 automaticFreshObservation.Items.Single().Result is { Conflicts: 0, Blocked: 0 });
            Equal(operationsAfterAutomaticUpload, await CountRowsAsync(authority.DatabasePath, "operation"));
            Equal(recoveryAfterAutomaticUpload, (await authority.LoadRecoveryArtifactsAsync()).Count);
            Equal(effectsAfterAutomaticUpload, (remote.UploadCount, remote.ArchiveCount,
                remote.VisibleCopyCount, remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount));

            PilotLocalEntry before;
            using (var root = PilotDirectoryLease.Open(pair.LocalRoot,
                       new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId)))
                before = (await new PilotLocalScanner().ScanAsync(root)).Entries["toggle.txt"];
            var remoteBefore = (await remote.ListEntriesAsync()).Single(item =>
                item.RelativePath == "toggle.txt");
            var pointBefore = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            var operationsBefore = await CountRowsAsync(authority.DatabasePath, "operation");
            var recoveryBefore = (await authority.LoadRecoveryArtifactsAsync()).Count;
            var writeEffectsBefore = (remote.UploadCount, remote.ArchiveCount, remote.VisibleCopyCount,
                remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount);

            Write(localRoot, "unbaselined.txt", "same-looking-but-unadopted");
            remote.PutWithId("unbaselined.txt", "same-looking-but-unadopted",
                "id:availability-unbaselined");
            var refusedUnbaselined = await host.SetPathAvailabilityAsync(pair.Id,
                "unbaselined.txt", keepOnDevice: false);
            True(refusedUnbaselined.Failures.Count == 1 &&
                 refusedUnbaselined.Failures[0].Contains("no exact two-sided synchronization point",
                     StringComparison.Ordinal));
            True(!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                Path.Combine(localRoot, "unbaselined.txt"), out _));

            var interruptedOnce = false;
            host.RepresentationAdoptionPhaseForTest = path =>
            {
                if (!interruptedOnce && string.Equals(path, "toggle.txt", StringComparison.OrdinalIgnoreCase))
                {
                    interruptedOnce = true;
                    throw new IOException("Injected failure after representation adoption and before conversion.");
                }
                return Task.CompletedTask;
            };
            await ThrowsAsync<IOException>(() => host.SetPathAvailabilityAsync(pair.Id,
                "toggle.txt", keepOnDevice: false));
            host.RepresentationAdoptionPhaseForTest = null;
            True(interruptedOnce);
            True(!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                Path.Combine(localRoot, "toggle.txt"), out _));
            var preadoptedPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            True(preadoptedPoint.LocalObjectVersionId != pointBefore.LocalObjectVersionId);
            var interruptedRun = await host.RunPairsAsync([pair],
                "Observe interrupted adoption before native conversion");
            True(interruptedRun.Items.Single().FailureMessage is null);
            var interruptedPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            Equal(preadoptedPoint.LocalObjectVersionId, interruptedPoint.LocalObjectVersionId);
            Equal(operationsBefore, await CountRowsAsync(authority.DatabasePath, "operation"));
            Equal(recoveryBefore, (await authority.LoadRecoveryArtifactsAsync()).Count);
            Equal(writeEffectsBefore, (remote.UploadCount, remote.ArchiveCount, remote.VisibleCopyCount,
                remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount));

            var firstFree = await host.SetPathAvailabilityAsync(pair.Id, "toggle.txt", keepOnDevice: false);
            True(firstFree.Failures.Count == 0);
            var afterFirstFree = await host.RunPairsAsync([pair], "Observe first Free up space");
            True(afterFirstFree.Items.Single().FailureMessage is null);
            var adoptedPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            True(adoptedPoint.LocalObjectVersionId != pointBefore.LocalObjectVersionId);
            var adoptedVersion = await authority.LoadObjectVersionAsync(adoptedPoint.LocalObjectVersionId!);
            using (var identity = JsonDocument.Parse(adoptedVersion.IdentityJson))
                True(!identity.RootElement.GetProperty("cloudFiles").TryGetProperty("availability", out _));

            var keep = await host.SetPathAvailabilityAsync(pair.Id, "toggle.txt", keepOnDevice: true);
            True(keep.Failures.Count == 0);
            var afterKeep = await host.RunPairsAsync([pair], "Observe Always keep on this device");
            True(afterKeep.Items.Single().FailureMessage is null);
            var keptPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            Equal(adoptedPoint.LocalObjectVersionId, keptPoint.LocalObjectVersionId);

            var secondFree = await host.SetPathAvailabilityAsync(pair.Id, "toggle.txt", keepOnDevice: false);
            True(secondFree.Failures.Count == 0);
            var afterSecondFree = await host.RunPairsAsync([pair], "Observe second Free up space");
            True(afterSecondFree.Items.Single().FailureMessage is null);
            var freedPoint = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            Equal(adoptedPoint.LocalObjectVersionId, freedPoint.LocalObjectVersionId);

            using (var root = PilotDirectoryLease.Open(pair.LocalRoot,
                       new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId)))
            {
                var after = (await new PilotLocalScanner().ScanAsync(root)).Entries["toggle.txt"];
                Equal(SyncCloudFileAvailability.OnlineOnly, after.CloudAvailability);
                Equal(before.VolumeSerialNumber, after.VolumeSerialNumber);
                Equal(before.FileId, after.FileId);
                Equal(before.LastWriteUtc, after.LastWriteUtc);
                Equal(before.ContentHash, after.ContentHash);
                Equal(before.Size, after.Size);
                Equal(remoteBefore.Id, after.CloudFileId);
                Equal(remoteBefore.Revision, after.CloudRevision);
            }
            var remoteAfter = (await remote.ListEntriesAsync()).Single(item =>
                item.RelativePath == "toggle.txt");
            Equal(remoteBefore.Id, remoteAfter.Id);
            Equal(remoteBefore.Revision, remoteAfter.Revision);
            Equal(remoteBefore.ContentHash, remoteAfter.ContentHash);
            Equal(remoteBefore.Size, remoteAfter.Size);
            Equal(operationsBefore, await CountRowsAsync(authority.DatabasePath, "operation"));
            Equal(recoveryBefore, (await authority.LoadRecoveryArtifactsAsync()).Count);
            Equal(writeEffectsBefore, (remote.UploadCount, remote.ArchiveCount, remote.VisibleCopyCount,
                remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount));

            var localRootBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "local");
            var remoteRootBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
            await new SyncAuthorityObservationCoordinator(pair, remote, authority,
                localRootBinding, remoteRootBinding).ObserveAsync("manual");
            var profileId = await ReadScalarAsync(authority.DatabasePath,
                "SELECT capability_profile_id FROM capability_profile ORDER BY created_utc DESC LIMIT 1;");
            var recoveryRootId = await ReadScalarAsync(authority.DatabasePath,
                "SELECT recovery_root_binding_id FROM recovery_root_binding WHERE side='dropbox' ORDER BY verified_utc DESC LIMIT 1;");
            var options = new SyncAuthorityPlannerOptions(pair.PolicyRevision, profileId,
                pair.ControlNamespace!.NamespaceId, recoveryRootId, EnableDeletionEffects: true,
                EnableMoveEffects: true, DeletionMinimumCompleteObservations: 1,
                DeletionMinimumInterval: TimeSpan.Zero);
            var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            var availabilityPlan = await new SyncAuthorityReconciliationPlanner(authority, options, admission: admission)
                .PlanAsync(pair.Id);
            var availabilityOutcome = availabilityPlan.Single(item => item.RelativePath == "toggle.txt");
            Equal(SyncAuthorityPlannerOutcomeKind.Unchanged, availabilityOutcome.Kind);
            True(availabilityOutcome.OperationId is null);

            // Both controls retain ordinary content/version sensitivity. They occur only
            // after the no-effect availability assertions above.
            Write(localRoot, "local-change.txt", "local-genuine-change");
            remote.Put("remote-change.txt", "remote-genuine-change");
            await new SyncAuthorityObservationCoordinator(pair, remote, authority,
                localRootBinding, remoteRootBinding).ObserveAsync("monitor");
            var changePlan = await new SyncAuthorityReconciliationPlanner(authority, options, admission: admission)
                .PlanAsync(pair.Id);
            var localChange = changePlan.Single(item => item.RelativePath == "local-change.txt");
            var remoteChange = changePlan.Single(item => item.RelativePath == "remote-change.txt");
            Equal(SyncAuthorityPlannerOutcomeKind.Operation, localChange.Kind);
            Equal(SyncAuthorityPlannerOutcomeKind.Operation, remoteChange.Kind);
            True(localChange.OperationId is not null && remoteChange.OperationId is not null);

            // A false adoption is rejected before any synchronization-point mutation.
            var pointBeforeRejection = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            await ThrowsAsync<InvalidOperationException>(() => authority.AdoptLocalCloudRepresentationAsync(
                pair.Id, "toggle.txt", adoptedVersion.IdentityJson, adoptedVersion.IdentityJson,
                remoteBefore.Id!, "wrong-revision", remoteBefore.ContentHash!, remoteBefore.Size));
            var pointAfterRejection = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id)).Single(point =>
                string.Equals(point.LocalRelativePath, "toggle.txt", StringComparison.OrdinalIgnoreCase));
            Equal(pointBeforeRejection.SynchronizationPointId, pointAfterRejection.SynchronizationPointId);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                proof = "SYNC-SHARK-RECOVERY-CORRECTION-FINAL-020",
                firstUseRepresentationAdopted = true,
                interruptedRepresentationRecovered = true,
                toggleSequence = new[] { "OnlineOnly", "AvailableOffline", "OnlineOnly" },
                stableObjectVersionId = adoptedPoint.LocalObjectVersionId,
                planner = availabilityOutcome.Kind.ToString(),
                operationId = availabilityOutcome.OperationId,
                remoteWrites = new
                {
                    uploads = remote.UploadCount,
                    archives = remote.ArchiveCount,
                    visibleCopies = remote.VisibleCopyCount,
                    deletes = remote.DeleteCount,
                    moves = remote.MoveCount,
                    directoryCreates = remote.DirectoryCreateCount
                },
                hydrationReads = new { remote.DownloadCount, remote.RangeDownloadCount },
                positiveControls = new[] { localChange.Kind.ToString(), remoteChange.Kind.ToString() }
            }));
            Console.WriteLine("PASS availability identity separation, first-use adoption, native toggles, runner convergence, and genuine-change controls");
        }
        finally
        {
            host.Dispose();
            if (WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id))
            {
                var result = WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(pair.LocalRoot);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
            }
            SqliteConnection.ClearAllPools();
        }

        static async Task<long> CountRowsAsync(string databasePath, string table)
        {
            var sql = table switch
            {
                "operation" => "SELECT count(*) FROM operation;",
                _ => throw new ArgumentOutOfRangeException(nameof(table))
            };
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        static async Task<string> ReadScalarAsync(string databasePath, string sql)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ??
                   throw new InvalidDataException("The required authority scalar is absent.");
        }
    });
}

static async Task WindowsCloudFilesContract()
{
    if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out _))
        throw new PlatformNotSupportedException("This contract requires the Windows Cloud Files platform.");
    if (Marshal.SizeOf<WindowsCloudFilesProvider.Native.FileBasicInfo>() != 40 ||
        Marshal.SizeOf<WindowsCloudFilesProvider.Native.FsMetadata>() != 48 ||
        Marshal.SizeOf<WindowsCloudFilesProvider.Native.OperationInfo>() != 32 ||
        Marshal.SizeOf<WindowsCloudFilesProvider.Native.OperationParameters>() != 40)
        throw new InvalidDataException("The managed Cloud Files ABI layout differs from the Windows SDK contract.");

    var root = Path.Combine(Path.GetTempPath(), "sync-shark-cf-" + Guid.NewGuid().ToString("N"));
    var temporary = Path.Combine(Path.GetTempPath(), "sync-shark-cf-stage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(temporary);
    WindowsCloudFilesProvider? provider = null;
    try
    {
        PilotDirectoryIdentity localIdentity;
        using (var lease = PilotDirectoryLease.Open(root)) localIdentity = lease.Identity;
        var remote = new FakeSyncRemoteStore();
        remote.Put("cloud.txt", "exact cloud bytes");
        var pair = new SyncPair("cloud-files-contract", "Cloud Files contract", root,
            localIdentity.VolumeSerialNumber, localIdentity.FileId, "/Cloud Files Contract",
            "id:cloud-files-contract", FilesOnDemandEnabled: true);
        provider = new WindowsCloudFilesProvider(pair, remote, temporary);
        provider.RegisterAndConnect();
        if (!provider.RegisteredByThisInstance || !provider.IsConnected ||
            !WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id))
            throw new IOException("The isolated Cloud Files sync root did not register and connect exactly.");
        var remoteEntry = (await remote.ListEntriesAsync()).Single();
        var placeholder = provider.CreatePlaceholder(remoteEntry);
        if (placeholder.Identity.DropboxRevision != remoteEntry.Revision ||
            placeholder.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            remote.DownloadCount != 0 ||
            new FileInfo(Path.Combine(root, "cloud.txt")).Length != remoteEntry.Size)
            throw new IOException("The non-hydrated placeholder metadata did not match Dropbox: " +
                                  $"revision={placeholder.Identity.DropboxRevision}/{remoteEntry.Revision}; " +
                                  $"availability={placeholder.State.Availability}; " +
                                  $"length={new FileInfo(Path.Combine(root, "cloud.txt")).Length}/{remoteEntry.Size}; " +
                                  $"onDisk={placeholder.State.OnDiskBytes}; pinned={placeholder.State.IsPinned}; " +
                                  $"rawState=0x{placeholder.RawPlaceholderState:x8}; " +
                                  $"downloads={remote.DownloadCount}.");
        using (var scanLease = PilotDirectoryLease.Open(root, localIdentity))
        {
            var scan = await new PilotLocalScanner().ScanAsync(scanLease,
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            var localEntry = scan.Entries["cloud.txt"];
            if (localEntry.CloudAvailability != SyncCloudFileAvailability.OnlineOnly ||
                localEntry.ContentHash != remoteEntry.ContentHash || localEntry.Size != remoteEntry.Size ||
                remote.DownloadCount != 0)
                throw new IOException("Local inventory hydrated or misidentified an online-only placeholder.");
        }
        var hydration = await provider.SetAvailabilityAsync("cloud.txt", keepOnDevice: true,
            recursive: false);
        if (hydration.Failures.Count != 0 || hydration.FilesChanged != 1 ||
            File.ReadAllText(Path.Combine(root, "cloud.txt")) != "exact cloud bytes" ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "cloud.txt"),
                out var hydrated) || hydrated is null ||
            hydrated.State.Availability != SyncCloudFileAvailability.AvailableOffline ||
            !hydrated.State.IsPinned)
            throw new IOException("The exact Dropbox revision did not hydrate and pin correctly.");
        var dehydration = await provider.SetAvailabilityAsync("cloud.txt", keepOnDevice: false,
            recursive: false);
        WindowsCloudPlaceholderInfo? dehydrated = null;
        if (dehydration.Failures.Count != 0 || dehydration.FilesChanged != 1 ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "cloud.txt"),
                out dehydrated) || dehydrated is null ||
            dehydrated.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            dehydrated.State.IsPinned)
            throw new IOException("The synchronized placeholder did not return to online-only state: " +
                                  $"availability={dehydrated?.State.Availability}; " +
                                  $"onDisk={dehydrated?.State.OnDiskBytes}; pinned={dehydrated?.State.IsPinned}; " +
                                  $"rawState=0x{dehydrated?.RawPlaceholderState:x8}.");

        WindowsCloudFilesProvider.SufficientDiskSpaceForTest = (_, _) => false;
        var insufficientSpace = await provider.SetAvailabilityAsync("cloud.txt", keepOnDevice: true,
            recursive: false);
        WindowsCloudFilesProvider.SufficientDiskSpaceForTest = null;
        if (insufficientSpace.Failures.Count != 1 ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "cloud.txt"),
                out var stillOnlineOnly) || stillOnlineOnly is null ||
            stillOnlineOnly.State.Availability != SyncCloudFileAvailability.OnlineOnly)
            throw new IOException("Insufficient disk space did not leave the placeholder safely online-only.");

        remote.InterruptNextDownload = true;
        var offlineHydration = await provider.SetAvailabilityAsync("cloud.txt", keepOnDevice: true,
            recursive: false);
        if (offlineHydration.Failures.Count != 1 ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "cloud.txt"),
                out var offlinePlaceholder) || offlinePlaceholder is null ||
            offlinePlaceholder.State.Availability == SyncCloudFileAvailability.AvailableOffline &&
            offlinePlaceholder.State.OnDiskBytes == offlinePlaceholder.State.LogicalSize)
            throw new IOException("Offline hydration did not fail visibly before claiming a complete offline copy.");

        remote.Put("cloud.txt", "new exact cloud bytes");
        var updatedRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "cloud.txt");
        var downloadsBeforeMetadataUpdate = remote.DownloadCount;
        var updated = provider.UpdatePlaceholder(updatedRemote, dehydrate: true);
        if (updated.FileId != placeholder.FileId ||
            updated.Identity.DropboxRevision != updatedRemote.Revision ||
            updated.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            remote.DownloadCount != downloadsBeforeMetadataUpdate)
            throw new IOException("A cloud-only metadata update hydrated content or changed file identity.");
        var updatedHydration = await provider.SetAvailabilityAsync("cloud.txt", keepOnDevice: true,
            recursive: false);
        if (updatedHydration.Failures.Count != 0 ||
            File.ReadAllText(Path.Combine(root, "cloud.txt")) != "new exact cloud bytes")
            throw new IOException("The updated placeholder did not hydrate the new exact revision.");
        WindowsCloudFilesProvider.BeforeDehydrateForTest =
            _ => throw new IOException("simulated interruption between unpin and dehydration");
        var interruptedDehydration = await provider.SetAvailabilityAsync("cloud.txt",
            keepOnDevice: false, recursive: false);
        WindowsCloudFilesProvider.BeforeDehydrateForTest = null;
        if (interruptedDehydration.Failures.Count != 1 ||
            File.ReadAllText(Path.Combine(root, "cloud.txt")) != "new exact cloud bytes")
            throw new IOException("Interrupted dehydration did not preserve the fully hydrated bytes.");
        var retriedDehydration = await provider.SetAvailabilityAsync("cloud.txt",
            keepOnDevice: false, recursive: false);
        if (retriedDehydration.Failures.Count != 0)
            throw new IOException("Interrupted dehydration did not converge on retry.");

        var largeBytes = Enumerable.Repeat((byte)0x4d, 16 * 1024 * 1024).ToArray();
        remote.PutBytes("large-range.bin", largeBytes);
        var largeRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "large-range.bin");
        provider.CreatePlaceholder(largeRemote);
        var rangeCallsBefore = remote.RangeDownloadCount;
        var rangeBytesBefore = remote.RangeDownloadBytes;
        var fullDownloadsBefore = remote.DownloadCount;
        var observedRange = new byte[4096];
        using (var rangeReader = new FileStream(Path.Combine(root, "large-range.bin"), FileMode.Open,
                   FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            rangeReader.Position = 4 * 1024 * 1024;
            rangeReader.ReadExactly(observedRange);
        }
        var transferredRangeBytes = remote.RangeDownloadBytes - rangeBytesBefore;
        if (observedRange.Any(value => value != 0x4d) ||
            remote.RangeDownloadCount <= rangeCallsBefore ||
            transferredRangeBytes is <= 0 || transferredRangeBytes > largeRemote.Size ||
            remote.DownloadCount != fullDownloadsBefore)
            throw new IOException(
                "A partial Cloud Files read did not remain on the bounded range-download path: " +
                $"rangeCalls={remote.RangeDownloadCount - rangeCallsBefore}; " +
                $"rangeBytes={transferredRangeBytes}/{largeRemote.Size}; " +
                $"fullDownloads={remote.DownloadCount - fullDownloadsBefore}; " +
                $"bytesMatch={observedRange.All(value => value == 0x4d)}.");

        var folderEntry = new PilotRemoteEntry("Folder", PilotEntryKind.Directory,
            "id:folder", null, 0, null, DateTimeOffset.UtcNow);
        provider.CreatePlaceholder(folderEntry);
        remote.Put("Folder/nested.txt", "nested cloud bytes");
        var nestedRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "Folder/nested.txt");
        provider.CreatePlaceholder(nestedRemote);
        var keepFolder = await provider.SetAvailabilityAsync("Folder", keepOnDevice: true,
            recursive: true);
        if (keepFolder.Failures.Count != 0 || keepFolder.FilesChanged != 1 ||
            File.ReadAllText(Path.Combine(root, "Folder", "nested.txt")) != "nested cloud bytes")
            throw new IOException("Recursive folder hydration did not keep the complete subtree offline.");
        var freeFolder = await provider.SetAvailabilityAsync("Folder", keepOnDevice: false,
            recursive: true);
        if (freeFolder.Failures.Count != 0 || freeFolder.FilesChanged != 1 ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                Path.Combine(root, "Folder", "nested.txt"), out var freedNested) ||
            freedNested is null ||
            freedNested.State.Availability != SyncCloudFileAvailability.OnlineOnly)
            throw new IOException("Recursive folder dehydration did not free its synchronized subtree.");

        remote.Put("converted.txt", "already synchronized bytes");
        pair = pair with { DefaultStorage = SyncDirectoryStorage.AlwaysAvailable,
            DirectoryStorageRules = [new("Folder/Archive", SyncDirectoryStorage.CloudOnly)] };
        provider.UpdatePairPolicy(pair);
        remote.PutDirectoryWithId("Folder", "id:folder");
        remote.PutDirectory("Folder/New Child"); remote.PutDirectory("Folder/Archive");
        remote.Put("Folder/New Child/kept.txt", "future child offline");
        remote.Put("Folder/Archive/cloud-only.txt", "archive stays in cloud");
        var policyEntries = (await remote.ListEntriesAsync()).Where(item => item.RelativePath.StartsWith("Folder/New Child", StringComparison.Ordinal) ||
            item.RelativePath.StartsWith("Folder/Archive", StringComparison.Ordinal)).OrderBy(item => item.RelativePath.Count(c => c == '/')).ToArray();
        foreach (var item in policyEntries) provider.CreatePlaceholder(item);
        var selectedStorage = await provider.SetAvailabilityAsync("Folder", true, true, includePath:
            path => SyncDirectoryStoragePolicy.Resolve(pair, path) == SyncDirectoryStorage.AlwaysAvailable);
        if (selectedStorage.Failures.Count != 0 ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "Folder", "New Child"), out var offlineDirectory) || offlineDirectory is null || !offlineDirectory.State.IsPinned ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "Folder", "Archive"), out var cloudDirectory) || cloudDirectory is null || cloudDirectory.State.IsPinned ||
            File.ReadAllText(Path.Combine(root, "Folder", "New Child", "kept.txt")) != "future child offline" ||
            !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "Folder", "Archive", "cloud-only.txt"), out var cloudException) || cloudException is null ||
            cloudException.State.Availability != SyncCloudFileAvailability.OnlineOnly)
            throw new IOException("Native mixed directory policy lost an offline inheritance or hydrated a cloud-only exception.");
        Console.WriteLine("PASS Native future-directory offline inheritance and cloud-only child exception");
        pair = pair with { DefaultStorage = SyncDirectoryStorage.CloudOnly, DirectoryStorageRules = [] };
        provider.UpdatePairPolicy(pair);
        File.WriteAllText(Path.Combine(root, "converted.txt"), "already synchronized bytes");
        var convertedRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "converted.txt");
        var downloadsBeforeConversion = remote.DownloadCount;
        var converted = await provider.ConvertVerifiedSyncedFileAsync(convertedRemote);
        if (converted.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            remote.DownloadCount != downloadsBeforeConversion)
            throw new IOException("A verified regular file did not convert to cloud-only without downloading.");

        remote.Put("changed-after-upload.txt", "uploaded bytes");
        File.WriteAllText(Path.Combine(root, "changed-after-upload.txt"), "uploaded bytes");
        var changedAfterUploadRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "changed-after-upload.txt");
        WindowsCloudFilesProvider.BeforeDehydrateForTest = path => File.AppendAllText(path, " changed");
        await ThrowsAsync<IOException>(() =>
            provider.ConvertVerifiedSyncedFileAsync(changedAfterUploadRemote));
        WindowsCloudFilesProvider.BeforeDehydrateForTest = null;
        if (File.ReadAllText(Path.Combine(root, "changed-after-upload.txt")) != "uploaded bytes changed" ||
            WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "changed-after-upload.txt"),
                out _))
            throw new IOException("A file changed after upload was converted or its local bytes were lost.");

        remote.Put("pending.txt", "uploaded version");
        var pendingRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "pending.txt");
        provider.CreatePlaceholder(pendingRemote);
        var pendingHydration = await provider.SetAvailabilityAsync("pending.txt", keepOnDevice: true,
            recursive: false);
        if (pendingHydration.Failures.Count != 0)
            throw new IOException("The pending-upload fixture did not hydrate.");
        File.AppendAllText(Path.Combine(root, "pending.txt"), " with local change");
        if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(Path.Combine(root, "pending.txt"),
                out var pendingPlaceholder) || pendingPlaceholder is null || pendingPlaceholder.State.IsInSync)
            throw new IOException("A local placeholder edit did not enter the pending-upload state.");
        var pendingDehydration = await provider.SetAvailabilityAsync("pending.txt",
            keepOnDevice: false, recursive: false);
        if (pendingDehydration.Failures.Count != 1 ||
            File.ReadAllText(Path.Combine(root, "pending.txt")) != "uploaded version with local change")
            throw new IOException("A pending upload was dehydrated or its local bytes were changed.");
        remote.Put("pending.txt", "uploaded version with local change");
        var uploadedPendingRemote = (await remote.ListEntriesAsync()).Single(item =>
            item.RelativePath == "pending.txt");
        var uploadedPending = await provider.ConvertVerifiedSyncedFileAsync(uploadedPendingRemote);
        if (uploadedPending.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            !uploadedPending.State.IsInSync)
            throw new IOException("A verified placeholder upload did not become safely online-only.");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => provider.SetAvailabilityAsync("cloud.txt",
                keepOnDevice: true, recursive: false, cancelled.Token));
        }
        if (!provider.IsConnected || !WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id))
            throw new IOException("A cancelled hydration detached or orphaned the Cloud Files root.");

        // Process restart/update: disposing disconnects callbacks but preserves the durable root;
        // the successor must verify exact ownership before updating and reconnecting it.
        provider.Dispose();
        provider = new WindowsCloudFilesProvider(pair, remote, temporary);
        provider.RegisterAndConnect();
        if (provider.RegisteredByThisInstance || !provider.IsConnected ||
            !WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id))
            throw new IOException("A successor process did not reconnect the exact retained Cloud Files root: " +
                                  $"new={provider.RegisteredByThisInstance}; connected={provider.IsConnected}; " +
                                  $"owned={WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id)}.");

        remote.InterruptNextDownload = true;
        await ThrowsAsync<IOException>(() => provider.UnregisterAsync());
        if (!provider.IsConnected || !WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id))
            throw new IOException(
                "Failed uninstall hydration did not preserve the registered and connected rollback state.");
        await provider.UnregisterAsync();
        if (WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id) ||
            File.ReadAllText(Path.Combine(root, "cloud.txt")) != "new exact cloud bytes" ||
            File.ReadAllText(Path.Combine(root, "Folder", "nested.txt")) != "nested cloud bytes" ||
            File.ReadAllText(Path.Combine(root, "converted.txt")) != "already synchronized bytes" ||
            new FileInfo(Path.Combine(root, "large-range.bin")).Length != largeRemote.Size ||
            File.ReadAllText(Path.Combine(root, "pending.txt")) != "uploaded version with local change")
            throw new IOException("Cloud Files unregistration did not preserve fully hydrated user content.");
        provider.Dispose();
        provider = null;

        var rollbackRoot = Path.Combine(root, "registration-rollback");
        Directory.CreateDirectory(rollbackRoot);
        File.WriteAllText(Path.Combine(rollbackRoot, "ordinary.txt"), "ordinary bytes");
        PilotDirectoryIdentity rollbackIdentity;
        using (var lease = PilotDirectoryLease.Open(rollbackRoot)) rollbackIdentity = lease.Identity;
        var rollbackPair = new SyncPair("cloud-files-registration-rollback", "Registration rollback",
            rollbackRoot, rollbackIdentity.VolumeSerialNumber, rollbackIdentity.FileId,
            "/Cloud Files Rollback", "id:cloud-files-rollback", FilesOnDemandEnabled: true);
        using (var rollbackProvider = new WindowsCloudFilesProvider(rollbackPair, remote, temporary))
        {
            rollbackProvider.RegisterAndConnect();
            if (!rollbackProvider.RegisteredByThisInstance) throw new IOException("Rollback root was not new.");
            rollbackProvider.RollBackNewRegistration();
            if (WindowsCloudFilesProvider.IsOwnedSyncRoot(rollbackRoot, rollbackPair.Id) ||
                File.ReadAllText(Path.Combine(rollbackRoot, "ordinary.txt")) != "ordinary bytes")
                throw new IOException("New-registration rollback changed user data or retained the root.");
        }
    }
    finally
    {
        WindowsCloudFilesProvider.SufficientDiskSpaceForTest = null;
        WindowsCloudFilesProvider.BeforeDehydrateForTest = null;
        if (provider is not null)
        {
            try { provider.RollBackNewRegistration(); }
            catch { }
            provider.Dispose();
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
}

static Task Reviewer2RejectionPreflight()
{
    var engine = RepositorySource("src/SyncShark.Core/BidirectionalSyncEngine.cs");
    var provider = RepositorySource("src/SyncShark.Core/DropboxProvider.cs");
    var browser = RepositorySource("src/SyncShark.Core/DropboxBrowser.cs");
    var localRecovery = RepositorySource("src/SyncShark.Core/SyncLocalRecovery.cs");
    var transaction = RepositorySource("src/SyncShark.Core/SyncLocalTransactionDirectory.cs");
    var controlNamespace = RepositorySource("src/SyncShark.Core/SyncControlNamespace.cs");
    var state = RepositorySource("src/SyncShark.Core/SyncState.cs");
    var models = RepositorySource("src/SyncShark.Core/SyncModels.cs");
    var mainWindow = RepositorySource("apps/SyncShark/MainWindow.xaml.cs");
    var runtimeHost = RepositorySource("src/SyncShark.Core/SyncAuthorityRuntimeHost.cs");
    var controlAllocator = RepositorySource("src/SyncShark.Core/SyncAuthorityLocalControlNamespaceAllocator.cs");
    var pairRunner = RepositorySource("src/SyncShark.Core/SyncAuthorityPairRunner.cs");
    var authority = RepositorySource("src/SyncShark.Core/SyncAuthorityDatabase.cs");
    var schemaSupplement = RepositorySource("src/SyncShark.Core/SyncAuthoritySchemaSupplement.cs");
    var runtimePaths = RepositorySource("src/SyncShark.Core/PilotSettings.cs");
    var tests = RepositorySource("tests/SyncShark.Core.Tests/Program.cs");
    var contractPreflight = RepositorySource("tests/SyncShark.Core.Tests/TrueSyncContractPreflight.cs");
    var witnessRegistry = RepositorySource("tests/SyncShark.Core.Tests/TrueSyncWitnessRegistry.cs");
    var preflightScript = RepositorySource("tools/Invoke-TrueSyncPreflight.ps1");
    var recoveryCopy = SourceSlice(provider,
        "public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath",
        "public async Task VerifyRecoveryRootAsync");
    var effectExecutors = string.Join("\n", new[]
    {
        "SyncAuthorityCreateLocalExecutor.cs", "SyncAuthorityCreateRemoteExecutor.cs",
        "SyncAuthorityDeleteLocalExecutor.cs", "SyncAuthorityDeleteRemoteExecutor.cs",
        "SyncAuthorityMoveLocalExecutor.cs", "SyncAuthorityMoveRemoteExecutor.cs",
        "SyncAuthorityPreserveConflictCopyExecutor.cs", "SyncAuthorityUpdateLocalExecutor.cs",
        "SyncAuthorityUpdateRemoteExecutor.cs"
    }.Select(name => RepositorySource("src/SyncShark.Core/" + name)));
    var pairIdDerivation = SourceSlice(models, "public static string DeriveId", "public static SyncPair FromPilot");

    Required("ordinary-transfer-identity-binding",
        engine.Contains("baseline.LocalLastWriteUtc", StringComparison.Ordinal) &&
        engine.Contains("baseline.RemoteId", StringComparison.Ordinal) &&
        engine.Contains("MatchesLocalIntentSource", StringComparison.Ordinal) &&
        engine.Contains("ExpectedSourceId", StringComparison.Ordinal) &&
        models.Contains("ExpectedSourceFileId", StringComparison.Ordinal));

    Required("revision-safe-dropbox-recovery",
        recoveryCopy.Contains("GetFileMetadataAsync(expected.Id", StringComparison.Ordinal) &&
        recoveryCopy.Contains("expected.Revision", StringComparison.Ordinal) &&
        recoveryCopy.Contains("DownloadToBoundedSpoolAsync(expected", StringComparison.Ordinal) &&
        recoveryCopy.Contains("VerifiedSpoolDownload(expected", StringComparison.Ordinal) &&
        recoveryCopy.Contains("UploadExactPathAsync", StringComparison.Ordinal) &&
        !recoveryCopy.Contains("/files/move_v2", StringComparison.Ordinal));

    Required("owned-control-namespaces",
        controlNamespace.Contains("OwnershipRecord", StringComparison.Ordinal) &&
        controlNamespace.Contains("MarkerFileId", StringComparison.Ordinal) &&
        controlNamespace.Contains("RequireRemotePairRootAllowed", StringComparison.Ordinal) &&
        !localRecovery.Contains("Directory.CreateDirectory(recoveryRoot)", StringComparison.Ordinal));

    Required("handle-bound-transaction-lifecycle",
        transaction.Contains("PilotDirectoryLease.CreateChild", StringComparison.Ordinal) &&
        transaction.Contains("lease.DeleteOnClose()", StringComparison.Ordinal) &&
        !transaction.Contains("Directory.Delete(path", StringComparison.Ordinal));

    Required("recovery-first-aggregate-scheduling",
        pairRunner.IndexOf("RecoverRunnableOperationsAsync", StringComparison.Ordinal) <
        pairRunner.IndexOf("for (var pass", StringComparison.Ordinal) &&
        authority.Contains("ORDER BY recovery_required DESC", StringComparison.Ordinal) &&
        authority.Contains("WHEN 'effect_unknown' THEN 0", StringComparison.Ordinal));

    Required("same-volume-owned-replacement-staging",
        engine.Contains("SyncLocalTransactionDirectory.Prepare(controlRoot", StringComparison.Ordinal) &&
        transaction.Contains("StagingRootVolumeSerialNumber = controlRoot.Identity.VolumeSerialNumber", StringComparison.Ordinal) &&
        controlNamespace.Contains("not on the selected pair volume", StringComparison.Ordinal));

    var broadRegistry = SourceSlice(tests, "var tests = new (", "var passed = 0;");
    Required("complete-recovery-witness-registry",
        broadRegistry.Contains("WritableObjectCrashRecovery", StringComparison.Ordinal) &&
        broadRegistry.Contains("NearCeilingCrashRecovery", StringComparison.Ordinal) &&
        broadRegistry.Contains("CleanupArtifactIdentityMismatch", StringComparison.Ordinal));

    var bindPair = SourceSlice(runtimeHost, "public async Task<SyncPairBindingResult> BindPairAsync",
        "public async Task EnsurePairConfigurationMutableAsync");
    var preparePair = SourceSlice(runtimeHost, "private async Task<PreparedPair> PreparePairAsync",
        "private async Task<PairAuthorityRegistration> RegisterPairAuthorityAsync");
    Required("truthful-nonempty-pair-binding",
        !mainWindow.Contains("verify that it is empty", StringComparison.OrdinalIgnoreCase) &&
        bindPair.Contains("EnsureAuthorityInitializedAsync", StringComparison.Ordinal) &&
        bindPair.Contains("SyncAuthorityLocalControlNamespaceAllocator", StringComparison.Ordinal) &&
        !bindPair.Contains("BindControlNamespaceAsync", StringComparison.Ordinal) &&
        bindPair.Contains("SyncRemoteRecoveryRootBinding", StringComparison.Ordinal) &&
        engine.Contains("Two stable identity-bound observations", StringComparison.Ordinal));

    Required("current-sync-adapter-has-no-implicit-remote-control-folder-effect",
        !models.Contains("BindControlNamespaceAsync", StringComparison.Ordinal) &&
        !provider.Contains("BindControlNamespaceAsync", StringComparison.Ordinal) &&
        !provider.Contains("EnsureOwnedFolderAsync", StringComparison.Ordinal) &&
        !provider.Contains("CreatedFolder(", StringComparison.Ordinal) &&
        !bindPair.Contains("SyncRemoteControlNamespaceBinding", StringComparison.Ordinal) &&
        !bindPair.Contains("CreateDirectoryAsync", StringComparison.Ordinal) &&
        !bindPair.Contains("EnsureRecommendedRecoveryFolder", StringComparison.Ordinal) &&
        !preparePair.Contains("CreateDirectoryAsync", StringComparison.Ordinal) &&
        !preparePair.Contains("EnsureRecommendedRecoveryFolder", StringComparison.Ordinal));

    Required("user-directed-browser-folder-creation-remains-isolated",
        browser.Contains("public async Task<DropboxFolderEntry> CreateFolderAsync", StringComparison.Ordinal) &&
        browser.Contains("/files/create_folder_v2", StringComparison.Ordinal) &&
        !provider.Contains("new DropboxBrowserClient", StringComparison.Ordinal));

    Required("migration-backup-is-exact-version-2-only",
        authority.Contains("EnsureVersion2RollbackBackupAsync", StringComparison.Ordinal) &&
        authority.Contains("Version2RollbackDatabasePath", StringComparison.Ordinal) &&
        !authority.Contains("RecoverIncompleteBackupsAsync", StringComparison.Ordinal) &&
        !authority.Contains("OpenBackupWriteConnectionAsync", StringComparison.Ordinal) &&
        !authority.Contains("EnsureBackupRoots", StringComparison.Ordinal) &&
        !schemaSupplement.Contains("maintenance_job", StringComparison.Ordinal) &&
        !schemaSupplement.Contains("database_backup", StringComparison.Ordinal) &&
        !runtimePaths.Contains("AuthorityBackupRoot", StringComparison.Ordinal) &&
        !runtimePaths.Contains("AuthorityBackupDirectoryName", StringComparison.Ordinal));

    Required("strict-current-or-exact-v2-schema-startup",
        authority.Contains("InspectReadOnlySchemaAsync", StringComparison.Ordinal) &&
        authority.Contains("Mode = SqliteOpenMode.ReadOnly", StringComparison.Ordinal) &&
        authority.Contains("RequireExactSchemaAsync", StringComparison.Ordinal) &&
        authority.Contains("Automatic migration is disabled", StringComparison.Ordinal) &&
        authority.Contains("FileMode.CreateNew", StringComparison.Ordinal) &&
        authority.Contains("FreshCreateBoundaryForTest", StringComparison.Ordinal) &&
        authority.Contains("MigrationBoundaryForTest", StringComparison.Ordinal) &&
        authority.Contains("SyncAuthoritySchemaMigration3.PreviousVersion", StringComparison.Ordinal) &&
        authority.IndexOf("EnsureVersion2RollbackBackupAsync", StringComparison.Ordinal) <
        authority.IndexOf("v3-schema-applied-before-version-commit", StringComparison.Ordinal) &&
        !authority.Contains("Version1To2Sql", StringComparison.Ordinal) &&
        !authority.Contains("SqliteOpenMode.ReadWriteCreate,\n                    requireCurrentSchema: false", StringComparison.Ordinal));

    Required("provider-effects-behind-runtime-host",
        !mainWindow.Contains("new RefreshingDropboxAccessTokenSource", StringComparison.Ordinal) &&
        !mainWindow.Contains("new DropboxBrowserClient", StringComparison.Ordinal) &&
        !mainWindow.Contains("new DropboxPilotClient", StringComparison.Ordinal) &&
        !mainWindow.Contains("new DropboxConnectionCoordinator", StringComparison.Ordinal) &&
        !mainWindow.Contains("new DropboxLoopbackReceiver", StringComparison.Ordinal) &&
        !mainWindow.Contains("PilotDirectoryLease.Open", StringComparison.Ordinal) &&
        !mainWindow.Contains("SyncControlNamespace.BindLocal", StringComparison.Ordinal) &&
        runtimeHost.Contains("CreateFolderBrowserAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("ConnectDropboxAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("BindPairAsync", StringComparison.Ordinal));

    Required("legacy-state-access-behind-runtime-host",
        !mainWindow.Contains("SyncPairStore", StringComparison.Ordinal) &&
        !mainWindow.Contains("SyncRuntimeStateStore", StringComparison.Ordinal) &&
        !mainWindow.Contains("SyncAutomationStore", StringComparison.Ordinal) &&
        !mainWindow.Contains("DropboxConnectionStore", StringComparison.Ordinal) &&
        !mainWindow.Contains("WindowsDropboxRefreshTokenStore", StringComparison.Ordinal) &&
        !mainWindow.Contains(".Authority.", StringComparison.Ordinal) &&
        runtimeHost.Contains("LoadPairs", StringComparison.Ordinal) &&
        runtimeHost.Contains("SelectPairAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("SaveAutomationAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("LoadActivityProjectionAsync", StringComparison.Ordinal));

    Required("sqlite-authoritative-pair-registry",
        runtimeHost.Contains("_authority.LoadPairSetAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("_authority.RetainPairDefinitionAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("RecordAcceptedLegacyPairImportAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("IsPairDefinitionRetainedAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("read-only v0.2.4 migration candidate", StringComparison.Ordinal) &&
        !runtimeHost.Contains("pairStore.UpsertAsync", StringComparison.Ordinal) &&
        !runtimeHost.Contains("pairStore.SelectAsync", StringComparison.Ordinal) &&
        !runtimeHost.Contains("pairStore.GetOrCreateInstallationIdAsync", StringComparison.Ordinal));

    Required("monitor-lifecycle-behind-runtime-host",
        !mainWindow.Contains("SyncAuthorityLocalMonitor", StringComparison.Ordinal) &&
        !mainWindow.Contains("SyncAuthorityDropboxMonitor", StringComparison.Ordinal) &&
        !mainWindow.Contains("_monitorDebounceTimer", StringComparison.Ordinal) &&
        !mainWindow.Contains("_monitorSyncPending", StringComparison.Ordinal) &&
        mainWindow.Contains("ConfigureMonitorsAsync", StringComparison.Ordinal) &&
        mainWindow.Contains("StopMonitorsAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("OnDurableMonitorHintAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("DeliverDebouncedMonitorRunAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("StopMonitorsCoreAsync", StringComparison.Ordinal));

    Required("durable-ui-command-lifecycle",
        authority.Contains("CreateCommandRequestAsync", StringComparison.Ordinal) &&
        authority.Contains("TransitionCommandRequestAsync", StringComparison.Ordinal) &&
        authority.Contains("RecoverInterruptedCommandRequestsAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("RunBatchCommandAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("CreateAcceptedCommandAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("TryBlockCommandAsync", StringComparison.Ordinal) &&
        mainWindow.Contains("TogglePauseActiveRunAsync", StringComparison.Ordinal) &&
        mainWindow.Contains("StopActiveRunAsync", StringComparison.Ordinal));

    Required("display-path-independent-pair-identity",
        pairIdDerivation.Contains("LocalVolumeSerialNumber", StringComparison.Ordinal) &&
        pairIdDerivation.Contains("LocalRootFileId", StringComparison.Ordinal) &&
        pairIdDerivation.Contains("DropboxFolderId", StringComparison.Ordinal) &&
        pairIdDerivation.Contains("providerRootNamespaceId", StringComparison.Ordinal) &&
        !pairIdDerivation.Contains("LocalRoot)", StringComparison.Ordinal) &&
        runtimeHost.Contains("SameRootIdentity(prior, pair)", StringComparison.Ordinal) &&
        runtimeHost.Contains("Id = prior.Id", StringComparison.Ordinal) &&
        runtimeHost.Contains("SelectionRules = prior.SelectionRules", StringComparison.Ordinal));

    Required("account-bound-dropbox-root-authority",
        authority.Contains("RegisterDropboxRootBindingAsync", StringComparison.Ordinal) &&
        authority.Contains("provider_account_id=$provider", StringComparison.Ordinal) &&
        authority.Contains("root_namespace_id=$root", StringComparison.Ordinal) &&
        runtimeHost.Contains("RegisterDropboxRootBindingAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("connection.RootNamespaceId", StringComparison.Ordinal));

    Required("volume-bound-local-root-authority",
        authority.Contains("RegisterLocalRootBindingAsync", StringComparison.Ordinal) &&
        authority.Contains("volume_id=$volume", StringComparison.Ordinal) &&
        authority.Contains("installation_id=$installation", StringComparison.Ordinal) &&
        authority.Contains("upper(volume_serial_hex)=upper($serial)", StringComparison.Ordinal) &&
        runtimeHost.Contains("RegisterLocalRootBindingAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("localFileIdHex", StringComparison.Ordinal));

    Required("duplicate-physical-root-rejection",
        authority.Contains("The physical local directory identity is already bound", StringComparison.Ordinal) &&
        authority.Contains("The Dropbox folder identity is already bound", StringComparison.Ordinal) &&
        authority.Contains("json_extract(identity_json,'$.volumeId')", StringComparison.Ordinal) &&
        authority.Contains("json_extract(identity_json,'$.providerAccountId')", StringComparison.Ordinal) &&
        authority.Contains("json_extract(identity_json,'$.folderId')", StringComparison.Ordinal));

    Required("generated-reviewed-contract-inventory",
        contractPreflight.Contains("ExpectedCases = 291", StringComparison.Ordinal) &&
        contractPreflight.Contains("ExpectedTransitions = 71", StringComparison.Ordinal) &&
        contractPreflight.Contains("ExpectedFailureRows = 180", StringComparison.Ordinal) &&
        contractPreflight.Contains("TEST-{caseId}-MODEL", StringComparison.Ordinal) &&
        contractPreflight.Contains("TEST-{caseId}-CRASH", StringComparison.Ordinal) &&
        contractPreflight.Contains("TEST-{caseId}-UI", StringComparison.Ordinal) &&
        contractPreflight.Contains("generated_case_tests_executed = 0", StringComparison.Ordinal) &&
        contractPreflight.Contains("case_specific_execution_claim = false", StringComparison.Ordinal) &&
        contractPreflight.Contains("witnessDependencies.Missing.Count == 0", StringComparison.Ordinal) &&
        contractPreflight.Contains("LoadWitnessPhaseResults", StringComparison.Ordinal) &&
        contractPreflight.Contains("production witness log hash does not match", StringComparison.Ordinal) &&
        contractPreflight.Contains("requirements-dependencies.json", StringComparison.Ordinal) &&
        witnessRegistry.Contains("structural generated-case to production-witness dependency mapping", StringComparison.Ordinal) &&
        witnessRegistry.Contains("ExecutionPhases", StringComparison.Ordinal) &&
        witnessRegistry.Contains("no concrete witness owner", StringComparison.Ordinal) &&
        preflightScript.Contains("status', '--porcelain=v1", StringComparison.Ordinal) &&
        preflightScript.Contains("--locked-mode", StringComparison.Ordinal) &&
        preflightScript.Contains("production-witness-execution.json", StringComparison.Ordinal) &&
        preflightScript.Contains("log_sha256", StringComparison.Ordinal) &&
        preflightScript.IndexOf("complete-test-registry", StringComparison.Ordinal) <
        preflightScript.IndexOf("contract-inventory", StringComparison.Ordinal) &&
        preflightScript.Contains("preflight-result.json", StringComparison.Ordinal));

    Required("run-control-behind-runtime-host",
        !mainWindow.Contains("PilotRunControl", StringComparison.Ordinal) &&
        !mainWindow.Contains(".RunPairAsync(", StringComparison.Ordinal) &&
        !mainWindow.Contains(".RecoverPairAsync(", StringComparison.Ordinal) &&
        !mainWindow.Contains(".ResolveConflictAsync(", StringComparison.Ordinal) &&
        mainWindow.Contains(".RunPairsAsync(", StringComparison.Ordinal) &&
        mainWindow.Contains(".RecoverPairsAsync(", StringComparison.Ordinal) &&
        mainWindow.Contains(".ResolveConflictSelectionCommandAsync(", StringComparison.Ordinal) &&
        runtimeHost.Contains("TogglePauseActiveRunAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("StopActiveRunAsync", StringComparison.Ordinal) &&
        runtimeHost.Contains("RunBatchCommandAsync", StringComparison.Ordinal));

    Required("atomic-dispatch-intent",
        authority.Contains("StartDispatchedAttemptAsync", StringComparison.Ordinal) &&
        !authority.Contains("StartAttemptAsync", StringComparison.Ordinal) &&
        !authority.Contains("MarkRequestSentAsync", StringComparison.Ordinal) &&
        !effectExecutors.Contains("StartAttemptAsync", StringComparison.Ordinal) &&
        !effectExecutors.Contains("MarkRequestSentAsync", StringComparison.Ordinal));

    Required("sqlite-authorized-local-control-allocation",
        bindPair.Contains("RegisterPairAuthorityAsync", StringComparison.Ordinal) &&
        bindPair.IndexOf("RegisterPairAuthorityAsync", StringComparison.Ordinal) <
        bindPair.IndexOf("SyncAuthorityLocalControlNamespaceAllocator", StringComparison.Ordinal) &&
        controlAllocator.Contains("AuthorizeNamespaceAllocationAsync", StringComparison.Ordinal) &&
        controlAllocator.IndexOf("AuthorizeNamespaceAllocationAsync", StringComparison.Ordinal) <
        controlAllocator.IndexOf("BindAuthorizedLocalAsync", StringComparison.Ordinal) &&
        controlAllocator.Contains("StartDispatchedAttemptAsync", StringComparison.Ordinal) &&
        controlAllocator.Contains("RegisterControlNamespaceAsync", StringComparison.Ordinal) &&
        controlAllocator.Contains("BindPairControlNamespaceAsync", StringComparison.Ordinal));

    return Task.CompletedTask;
}

static void Required(string findingId, bool condition)
{
    if (!condition)
        throw new InvalidOperationException($"Successor safety assertion failed: {findingId}.");
    Console.WriteLine("PASS " + findingId);
}

static string RepositorySource(string relativePath)
{
    var root = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "SyncShark.sln")))
        root = root.Parent;
    if (root is null)
        throw new InvalidOperationException("Run the rejection preflight from the Sync Shark repository tree.");
    return File.ReadAllText(Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}

static string SourceSlice(string source, string startMarker, string endMarker)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException($"Source marker is absent: {startMarker}");
    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    if (end < 0) throw new InvalidOperationException($"Source marker is absent: {endMarker}");
    return source[start..end];
}

static string SourceSliceLast(string source, string startMarker, string endMarker)
{
    var start = source.LastIndexOf(startMarker, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException($"Source marker is absent: {startMarker}");
    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    if (end < 0) throw new InvalidOperationException($"Source marker is absent: {endMarker}");
    return source[start..end];
}

static async Task EmptyContentHash()
{
    await using var stream = new MemoryStream();
    Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", await DropboxContentHash.ComputeAsync(stream));
}

static async Task UploadOnlyAcceptance()
{
    await WithTempRoot(async root =>
    {
        Write(root, "upload.txt", "local upload");
        Write(root, "identical.txt", "same bytes");
        Write(root, "conflict.txt", "local conflict");
        var remote = new FakeRemoteStore(new Dictionary<string, byte[]>
        {
            ["identical.txt"] = Bytes("same bytes"),
            ["conflict.txt"] = Bytes("remote conflict"),
            ["remote-only.txt"] = Bytes("remote only")
        });
        var result = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(1, result.Uploaded);
        Equal(1, result.Identical);
        Equal(2, result.Conflicts);
        Equal(0, result.Blocked);
        Equal("local upload", Encoding.UTF8.GetString(remote.Files["upload.txt"]));
        Equal("local conflict", File.ReadAllText(Path.Combine(root, "conflict.txt")));
        Equal("remote conflict", Encoding.UTF8.GetString(remote.Files["conflict.txt"]));
        Equal(1, remote.UploadCount);
        True(remote.VerifyCount >= 3);
    });
}

static async Task RerunSkipsCompletedUploads()
{
    await WithTempRoot(async root =>
    {
        Write(root, "upload.txt", "local upload");
        Write(root, "identical.txt", "same bytes");
        var remote = new FakeRemoteStore(new Dictionary<string, byte[]> { ["identical.txt"] = Bytes("same bytes") });
        await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        var second = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(0, second.Uploaded);
        Equal(2, second.Identical);
        Equal(1, remote.UploadCount);
    });
}

static async Task NestedFoldersSynchronize()
{
    await WithTempRoot(async root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "subfolder"));
        Write(root, "subfolder/nested.txt", "nested file");
        Write(root, "upload.txt", "safe file");
        var remote = new FakeRemoteStore([]);
        var result = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(2, result.Uploaded);
        Equal(0, result.Blocked);
        Equal("nested file", Encoding.UTF8.GetString(remote.Files["subfolder/nested.txt"]));
    });
}

static Task LocalRootIdentity()
{
    return WithTempRoot(root =>
    {
        using var lease = PilotDirectoryLease.Open(root);
        lease.VerifyBinding();
        using var rebound = PilotDirectoryLease.Open(root, lease.Identity);
        var wrong = new PilotDirectoryIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId + 1);
        Throws<IOException>(() => PilotDirectoryLease.Open(root, wrong));
        return Task.CompletedTask;
    });
}

static async Task LargeLocalInventory()
{
    await WithTempRoot(async root =>
    {
        for (var index = 0; index <= TestScale.StressEntryCount; index++) Write(root, $"f{index:D3}.txt", string.Empty);
        var remote = new FakeRemoteStore([]);
        var result = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(TestScale.StressEntryCount + 1, result.Uploaded);
        Equal(0, result.Blocked);
        Equal(TestScale.StressEntryCount + 1, remote.UploadCount);
    });
}

static async Task LargeRemoteInventory()
{
    await WithTempRoot(async root =>
    {
        var files = Enumerable.Range(0, TestScale.StressEntryCount + 1)
            .ToDictionary(index => $"r{index:D3}.txt", _ => Array.Empty<byte>(), StringComparer.OrdinalIgnoreCase);
        var remote = new FakeRemoteStore(files);
        var result = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(0, result.Uploaded);
        Equal(TestScale.StressEntryCount + 1, result.Conflicts);
        Equal(0, result.Blocked);
    });
}

static async Task SelectiveSubtreeTraversal()
{
    await WithTempRoot(async root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "Included"));
        Directory.CreateDirectory(Path.Combine(root, "Excluded", "Ignored"));
        Directory.CreateDirectory(Path.Combine(root, "Excluded", "Reenabled"));
        Write(root, "Included/kept.txt", "kept");
        Write(root, "Excluded/Ignored/skipped.txt", "must not be hashed");
        Write(root, "Excluded/Reenabled/restored.txt", "restored");
        var selection = SyncSelectionPolicy.Compile([
            new SyncSelectionRule("Excluded", false, "dropbox:id:excluded"),
            new SyncSelectionRule("Excluded/Reenabled", true, "dropbox:id:reenabled")
        ]);
        using var lease = PilotDirectoryLease.Open(root);
        var scan = await new PilotLocalScanner().ScanAsync(lease,
            includePath: selection.IsIncluded,
            traverseDirectory: selection.ShouldTraverseDirectory);
        True(scan.Entries.ContainsKey("Included/kept.txt"));
        True(scan.Entries.ContainsKey("Excluded/Reenabled/restored.txt"));
        True(scan.Entries.TryGetValue("Excluded/Ignored", out var excludedCatalogFolder) &&
             excludedCatalogFolder.Kind == PilotEntryKind.Directory &&
             !scan.Entries.ContainsKey("Excluded/Ignored/skipped.txt"));
        Equal(2, scan.FilesHashed);
    });
}

static async Task FolderSelectionIndexPaging()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var remote = new FakeSyncRemoteStore();
        for (var index = 0; index < 520; index++)
        {
            var path = $"Folder-{index:D4}";
            Directory.CreateDirectory(Path.Combine(localRoot, path));
            remote.PutDirectory(path);
        }
        foreach (var path in new[] { "Excluded", "Excluded/Other", "Excluded/Reenabled" })
        {
            Directory.CreateDirectory(Path.Combine(localRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            remote.PutDirectory(path);
        }
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-folder-index", "Folder index", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Folder Index", "id:folder-index",
            SelectionRules:
            [
                new SyncSelectionRule("Excluded", false, "dropbox:id:folder:Excluded"),
                new SyncSelectionRule("Excluded/Reenabled", true,
                    "dropbox:id:folder:Excluded/Reenabled")
            ]);
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db"));
        await authority.InitializeAsync("installation-folder-index", "profile-folder-index",
            "folder-index-test", "{}");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-folder-index-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-folder-index-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:folder-index\"}", pair.DropboxRoot);
        await new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-folder-index-local", "root-folder-index-dropbox").ObserveAsync("manual");

        var first = await authority.LoadFolderSelectionPageAsync(pair.Id, 200, 0);
        True(first is not null && first.Folders.Count == 200 && first.HasMore &&
             first.TotalFolders == 523 && first.TotalMatchingFolders == 523);
        var second = await authority.LoadFolderSelectionPageAsync(pair.Id, 200, 200);
        True(second is not null && second.Folders.Count == 200 && second.Offset == 200 && second.HasMore);
        var search = await authority.LoadFolderSelectionPageAsync(pair.Id, 20, 0, "folder-0519");
        True(search is not null && search.TotalMatchingFolders == 1 &&
             search.Folders.Single().RelativePath == "Folder-0519");
        var excluded = await authority.LoadFolderSelectionPageAsync(pair.Id, 20, 0, null, "Excluded");
        True(excluded is not null && excluded.Folders.Any(item => item.RelativePath == "Excluded" &&
                                                                  item.IsChecked is null) &&
             excluded.Folders.Any(item => item.RelativePath == "Excluded/Other" &&
                                           item.IsChecked == false) &&
             excluded.Folders.All(item => item.RelativePath != "Excluded/Reenabled"));
    });
}

static async Task FolderHierarchyDegradationContract()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(Path.Combine(localRoot, "Local documents"));
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        SyncPair pair;
        using (var lease = PilotDirectoryLease.Open(localRoot))
        {
            pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/Folder Health", "id:folder-health-root"),
                "Folder health", connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true
            };
        }
        await pairStore.UpsertAsync(pair, select: true);
        var authorityPath = Path.Combine(root, "vNext", "state", "syncshark.db");
        using (var dropboxUnavailableHost = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"),
                   new SyncAuthorityDatabase(authorityPath),
                   (_, _) => throw new IOException("fixture Dropbox endpoint unavailable")))
        {
            var localOnlyTree = await dropboxUnavailableHost.LoadFolderSelectionTreeAsync(pair.Id);
            True(localOnlyTree.LocalHierarchyAvailable && !localOnlyTree.DropboxHierarchyAvailable);
            Equal(SyncFolderPresence.DropboxUnloaded,
                localOnlyTree.Folders.Single(item => item.RelativePath == "Local documents").Presence);
        }
        Directory.Delete(localRoot, recursive: true);
        var remote = new FakeSyncRemoteStore();
        remote.Put("root-file.txt", "root-level content");
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"),
            new SyncAuthorityDatabase(authorityPath),
            (_, _) => remote);

        var rootOnlyTree = await host.LoadFolderSelectionTreeAsync(pair.Id);
        True(!rootOnlyTree.LocalHierarchyAvailable && rootOnlyTree.DropboxHierarchyAvailable);
        Equal(0, rootOnlyTree.Folders.Count);
        Equal(null, rootOnlyTree.LocalRootFiles);
        Equal(1, rootOnlyTree.DropboxRootFiles);

        remote.PutDirectoryWithId("Documents", "id:folder-health-documents");
        var tree = await host.LoadFolderSelectionTreeAsync(pair.Id);
        True(!tree.LocalHierarchyAvailable && tree.DropboxHierarchyAvailable);
        True(tree.LocalHierarchyMessage is not null &&
             tree.LocalHierarchyMessage.Contains("Local folder unavailable", StringComparison.Ordinal));
        var documents = tree.Folders.Single(item => item.RelativePath == "Documents");
        Equal(SyncFolderPresence.LocalUnloaded, documents.Presence);
        Equal("id:folder-health-documents", documents.DropboxStableFolderIdentity);
        Equal(null, tree.LocalRootFiles);
        Equal(1, tree.DropboxRootFiles);
    });
}

static async Task ChangedOnlyHashing()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "unchanged.txt", "stable bytes");
        Write(localRoot, "changed.txt", "first bytes");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-changed-hash", "Changed-only hashing", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Changed Hash", "id:changed-hash");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db"));
        await authority.InitializeAsync("installation-changed-hash", "profile-changed-hash",
            "changed-hash-test", "{}");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-changed-hash-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-changed-hash-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:changed-hash\"}", pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, new FakeSyncRemoteStore(), authority,
            "root-changed-hash-local", "root-changed-hash-dropbox");

        var first = await observer.ObserveAsync("initial");
        Equal(2, first.LocalFilesHashed);
        Equal(0, first.LocalHashesReused);
        // The persisted "periodic" trigger selects a complete safety scan. A retained
        // (file identity, size, last-write) tuple is not freshness authority, so a complete
        // scan hashes the held bytes again instead of reusing saved hashes; retained unchanged
        // entries are reused only through the continuous journal/watcher index.
        var second = await observer.ObserveAsync("periodic");
        Equal(2, second.LocalFilesHashed);
        Equal(0, second.LocalHashesReused);

        var changedPath = Path.Combine(localRoot, "changed.txt");
        await File.WriteAllTextAsync(changedPath, "second and longer bytes");
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
        var third = await observer.ObserveAsync("periodic");
        Equal(2, third.LocalFilesHashed);
        Equal(0, third.LocalHashesReused);
    });
}

static async Task BootstrapPagingAndGrouping()
{
    await WithTempRoot(async root =>
    {
        const string pairId = "pair-bootstrap-scale";
        const string localRootId = "root-bootstrap-scale-local";
        const string dropboxRootId = "root-bootstrap-scale-dropbox";
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db"));
        await authority.InitializeAsync("installation-bootstrap-scale", "profile-bootstrap-scale",
            "bootstrap-scale-test", "{}");
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(localRootId, pairId, "local", 1,
            "{\"volumeSerial\":\"00000001\",\"fileId\":\"0000000000000001\"}", localRoot);
        await authority.RegisterRootBindingAsync(dropboxRootId, pairId, "dropbox", 1,
            "{\"folderId\":\"id:bootstrap-scale\"}", "/Bootstrap Scale");
        var localScan = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "initial",
            "{\"scope\":\"complete\"}");
        var dropboxScan = await authority.BeginScanGenerationAsync(pairId, dropboxRootId, "dropbox", "initial",
            "{\"scope\":\"complete\"}");
        var localEntries = new List<SyncAuthorityObservationInput>();
        var dropboxEntries = new List<SyncAuthorityObservationInput>();
        var index = new List<SyncPathIndexEntry>();
        var localTime = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        for (var item = 0; item < 3100; item++)
        {
            var path = item < 1000 ? $"Local/item-{item:D4}.txt"
                : item < 2000 ? $"Dropbox/item-{item:D4}.txt"
                : item < 3000 ? $"Conflict/item-{item:D4}.txt"
                : $"Identical/item-{item:D4}.txt";
            var localPresent = item < 1000 || item >= 2000;
            var dropboxPresent = item >= 1000;
            var relativeConflict = item - 2000;
            DateTimeOffset? localModified = localTime;
            DateTimeOffset? dropboxModified = localTime;
            if (item is >= 2000 and < 3000)
            {
                if (relativeConflict < 400) localModified = localTime.AddHours(3);
                else if (relativeConflict < 700) dropboxModified = localTime.AddHours(3);
                else if (relativeConflict >= 900) dropboxModified = null;
            }
            if (localPresent)
            {
                var identity = JsonSerializer.Serialize(new
                {
                    volumeSerial = "00000001",
                    fileId = item.ToString("x16"),
                    lastWriteUtc = localModified?.ToString("O")
                });
                localEntries.Add(new SyncAuthorityObservationInput(path, "file", identity,
                    item >= 3000 ? hashA : hashA, item + 1,
                    JsonSerializer.Serialize(new { path, kind = "file", identity, ContentHash = hashA, Size = item + 1 })));
            }
            if (dropboxPresent)
            {
                var remoteHash = item is >= 2000 and < 3000 ? hashB : hashA;
                dropboxEntries.Add(new SyncAuthorityObservationInput(path, "file",
                    JsonSerializer.Serialize(new { fileId = "id:" + item, revision = "rev:" + item }),
                    remoteHash, item + 1,
                    JsonSerializer.Serialize(new
                    {
                        path, kind = "file", ContentHash = remoteHash, Size = item + 1,
                        modifiedUtc = dropboxModified?.ToString("O")
                    })));
            }
            index.Add(new SyncPathIndexEntry(path, PilotEntryKind.File, true, localPresent,
                dropboxPresent, false, localPresent ? "local:" + item : null,
                dropboxPresent ? "id:" + item : null, true));
        }
        await authority.AppendObservationEntriesAsync(localScan, localEntries);
        await authority.AppendObservationEntriesAsync(dropboxScan, dropboxEntries);
        await authority.CompleteScanGenerationAsync(localScan);
        await authority.CompleteScanGenerationAsync(dropboxScan);
        await authority.ReplacePathIndexAsync(pairId, localScan.ScanGenerationId,
            dropboxScan.ScanGenerationId, index, SyncSelectionPolicy.Compile([]));

        var timer = Stopwatch.StartNew();
        var first = await authority.LoadBootstrapEvidencePageAsync(pairId, 200, 0);
        timer.Stop();
        True(first is not null && first.Items.Count == 200 && first.HasMore &&
             first.TotalItems == 3100 && first.LocalOnlyItems == 1000 &&
             first.DropboxOnlyItems == 1000 && first.ConflictItems == 1000 &&
             first.LocalScanGenerationId == localScan.ScanGenerationId &&
             first.DropboxScanGenerationId == dropboxScan.ScanGenerationId);
        True(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"The first 3,100-row bootstrap evidence page took {timer.Elapsed.TotalMilliseconds:N0} ms; the limit is 5,000 ms.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "bootstrap_evidence_first_page",
            itemCount = 3100,
            pageSize = 200,
            elapsedMilliseconds = timer.ElapsedMilliseconds,
            targetMilliseconds = 5000
        }));
        var localNewer = await authority.LoadBootstrapEvidencePageAsync(pairId, 200, 0,
            group: "Local newer");
        True(localNewer is not null && localNewer.TotalMatchingItems == 400 &&
             localNewer.Items.All(item => item.Group == "Local newer"));
        var classificationQueries = 0;
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "bootstrap-evidence-query") classificationQueries++;
        };
        try
        {
            var complete = await authority.LoadBootstrapEvidenceAsync(pairId,
                reviewedLocalScanId: localScan.ScanGenerationId,
                reviewedRemoteScanId: dropboxScan.ScanGenerationId);
            True(complete is { Items.Count: 3100, TotalItems: 3100, HasMore: false } &&
                complete.Items.Take(200).SequenceEqual(first!.Items) &&
                complete.LocalOnlyItems == first.LocalOnlyItems &&
                complete.DropboxOnlyItems == first.DropboxOnlyItems &&
                complete.ConflictItems == first.ConflictItems && classificationQueries == 1,
                "Complete reviewed evidence preserves all 3,100 rows and classifications with one query, not paged recomputation.");
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        await ThrowsAsync<ArgumentOutOfRangeException>(() => authority.LoadBootstrapEvidencePageAsync(pairId, 501));
        await ThrowsAsync<InvalidDataException>(() => authority.LoadBootstrapEvidenceAsync(pairId,
            reviewedLocalScanId: "foreign-review", reviewedRemoteScanId: dropboxScan.ScanGenerationId));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => authority.LoadBootstrapEvidenceAsync(pairId,
                cancellationToken: cancelled.Token));
        }
        var files = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0,
            itemKind: "Files");
        True(files is not null && files.TotalMatchingItems == 3100 &&
             files.Items.All(item => item.ObjectKind == "file"));
        var folders = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0,
            itemKind: "Folders");
        True(folders is { TotalMatchingItems: 0, Items.Count: 0 });
        var textFiles = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0,
            fileType: "TXT");
        True(textFiles is not null && textFiles.TotalMatchingItems == 3100);
        var searched = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0,
            "item-3099");
        True(searched is not null && searched.TotalMatchingItems == 1 &&
             searched.Items.Single().RelativePath == "Identical/item-3099.txt" &&
             searched.Items.Single().Group == "Identical");
        var noMatch = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0, "absent-item");
        True(noMatch is { TotalMatchingItems: 0, TotalItems: 3100, Items.Count: 0, HasMore: false });
        var beyondLastPage = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 4000);
        True(beyondLastPage is
        {
            TotalMatchingItems: 3100, TotalItems: 3100, Items.Count: 0, HasMore: false
        });

        // The shared full/paged classification must read exactly the two selected scans,
        // including their null-membership uncertainty, never earlier or another pair's rows.
        var baseline = await authority.LoadBootstrapEvidenceAsync(pairId,
            reviewedLocalScanId: localScan.ScanGenerationId,
            reviewedRemoteScanId: dropboxScan.ScanGenerationId);
        var historical = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "manual", "{}");
        await authority.AppendObservationAsync(historical, "bootstrap-history-uncertain", null, null,
            "{\"path\":\"Local/item-0000.txt\",\"state\":\"unavailable\"}");
        await authority.AppendObservationAsync(historical, "bootstrap-history-only", null, null,
            "{\"path\":\"history-only.txt\",\"state\":\"unavailable\"}");
        await authority.CompleteScanGenerationAsync(historical);
        const string foreignPair = "pair-bootstrap-foreign";
        const string foreignRoot = "root-bootstrap-foreign-local";
        await authority.RegisterPairAsync(foreignPair, 1);
        await ThrowsAsync<InvalidOperationException>(() => authority.RegisterRootBindingAsync(
            foreignRoot, foreignPair, "local", 1, "{}", localRoot));
        // A foreign pair needs its own non-overlapping root. Exercise the real
        // overlap rejection above, then prove scan isolation with a valid pair.
        var foreignLocalRoot = Path.Combine(root, "foreign");
        Directory.CreateDirectory(foreignLocalRoot);
        await authority.RegisterRootBindingAsync(foreignRoot, foreignPair, "local", 1, "{}", foreignLocalRoot);
        var foreignScan = await authority.BeginScanGenerationAsync(foreignPair, foreignRoot, "local", "manual", "{}");
        await authority.AppendObservationAsync(foreignScan, "bootstrap-foreign-uncertain", null, null,
            "{\"path\":\"foreign-only.txt\",\"state\":\"unavailable\"}");
        await authority.CompleteScanGenerationAsync(foreignScan);
        var pinned = await authority.LoadBootstrapEvidenceAsync(pairId,
            reviewedLocalScanId: localScan.ScanGenerationId,
            reviewedRemoteScanId: dropboxScan.ScanGenerationId);
        True(pinned is not null && baseline is not null && pinned.Items.SequenceEqual(baseline.Items) &&
             pinned.TotalItems == baseline.TotalItems && pinned.LocalOnlyItems == baseline.LocalOnlyItems &&
             pinned.DropboxOnlyItems == baseline.DropboxOnlyItems && pinned.ConflictItems == baseline.ConflictItems,
            "Historical and foreign uncertainty leave every pinned review row and aggregate unchanged.");
        await ThrowsAsync<InvalidDataException>(() => authority.LoadBootstrapEvidenceAsync(pairId,
            reviewedLocalScanId: foreignScan.ScanGenerationId,
            reviewedRemoteScanId: dropboxScan.ScanGenerationId));

        var currentLocal = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "manual", "{}");
        var currentRemote = await authority.BeginScanGenerationAsync(pairId, dropboxRootId, "dropbox", "manual", "{}");
        await authority.AppendObservationEntriesAsync(currentLocal, localEntries);
        await authority.AppendObservationEntriesAsync(currentRemote, dropboxEntries);
        await authority.AppendObservationAsync(currentLocal, "bootstrap-current-local-uncertain", null, null,
            "{\"path\":\"Local/item-0000.txt\",\"state\":\"unavailable\"}");
        await authority.AppendObservationAsync(currentLocal, "bootstrap-current-only-uncertain", null, null,
            "{\"path\":\"current-uncertain-only.txt\",\"state\":\"unavailable\"}");
        await authority.AppendObservationAsync(currentLocal, "bootstrap-current-excluded", null, null,
            "{\"path\":\"Local/item-0001.txt\",\"state\":\"selective_sync_excluded\"}");
        await authority.AppendObservationAsync(currentRemote, "bootstrap-current-remote-uncertain", null, null,
            "{\"path\":\"Dropbox/item-1000.txt\",\"state\":\"unavailable\"}");
        await authority.CompleteScanGenerationAsync(currentLocal);
        await authority.CompleteScanGenerationAsync(currentRemote);
        var current = await authority.LoadBootstrapEvidenceAsync(pairId);
        var unknown = await authority.LoadBootstrapEvidencePageAsync(pairId, 20, 0, group: "Unreliable or missing");
        True(current is { TotalItems: 3101, Items.Count: 3101 } &&
             current.Items.Single(item => item.RelativePath == "Local/item-0001.txt").Group == "Local only" &&
             current.Items.All(item => item.RelativePath is not ("history-only.txt" or "foreign-only.txt")) &&
             unknown is { TotalMatchingItems: 3, TotalItems: 3101, Items.Count: 3 } &&
             unknown.Items.SequenceEqual(current.Items.Where(item => item.Group == "Unreliable or missing")),
            "Both selected scans retain exact uncertainty in full/paged views; exclusions and unrelated history never become uncertainty.");

        const string replacementRoot = "root-bootstrap-scale-replacement";
        var replacementLocalRoot = Path.Combine(root, "replacement");
        Directory.CreateDirectory(replacementLocalRoot);
        // Registering another active root is not the supported pair-replacement
        // workflow. Prove that both a duplicate root and a changed-generation
        // replay are refused, rather than constructing an impossible live pair.
        await ThrowsAsync<SqliteException>(() => authority.RegisterRootBindingAsync(
            replacementRoot, pairId, "local", 2,
            "{\"volumeSerial\":\"00000002\",\"fileId\":\"0000000000000002\"}", replacementLocalRoot));
        await ThrowsAsync<InvalidOperationException>(() => authority.RegisterRootBindingAsync(
            localRootId, pairId, "local", 2,
            "{\"volumeSerial\":\"00000002\",\"fileId\":\"0000000000000002\"}", replacementLocalRoot));
        var afterRejectedRootChange = await authority.LoadBootstrapEvidenceAsync(pairId,
            reviewedLocalScanId: currentLocal.ScanGenerationId,
            reviewedRemoteScanId: currentRemote.ScanGenerationId);
        var pageAfterRejectedRootChange = await authority.LoadBootstrapEvidencePageAsync(pairId, 200, 0,
            reviewedLocalScanId: currentLocal.ScanGenerationId,
            reviewedRemoteScanId: currentRemote.ScanGenerationId);
        True(current is not null && afterRejectedRootChange is not null &&
             afterRejectedRootChange.Items.SequenceEqual(current.Items) &&
             afterRejectedRootChange.TotalItems == current.TotalItems &&
             pageAfterRejectedRootChange is { TotalMatchingItems: 3101, TotalItems: 3101 } &&
             pageAfterRejectedRootChange.Items.SequenceEqual(current.Items.Take(200)),
            "Rejected root replacement and generation replay leave current full/paged review evidence unchanged.");
    });
}

static async Task BootstrapReviewedDirectionFence()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "local-only.txt"), "reviewed upload");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-bootstrap-reviewed", "Reviewed first sync", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Reviewed First Sync", "id:reviewed-first-sync",
            InitialReviewApproved: false);
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-bootstrap-reviewed", "profile-bootstrap-reviewed",
            "bootstrap-reviewed-test", "{}");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-bootstrap-reviewed-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-bootstrap-reviewed-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:reviewed-first-sync\"}", pair.DropboxRoot);
        var remote = new FakeSyncRemoteStore();
        await new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-bootstrap-reviewed-local", "root-bootstrap-reviewed-dropbox").ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        await authority.SetPairStateAsync(pair.Id, "active");
        var reviewed = new HashSet<string>(["local-only.txt"], StringComparer.OrdinalIgnoreCase);
        var wrongDirection = new Dictionary<string, SyncAuthorityOperationKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-only.txt"] = SyncAuthorityOperationKind.CreateLocal
        };
        await authority.RegisterOperationGroupAsync("bootstrap-wrong-direction", "compound", "{}");
        await authority.MarkOperationGroupActiveAsync("bootstrap-wrong-direction");
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var rejectedProgress = new List<SyncComparisonProgress>();
        var rejected = await new SyncAuthorityPairRunner(pair, remote, authority,
                "root-bootstrap-reviewed-local", "root-bootstrap-reviewed-dropbox",
                new SyncAuthorityPlannerOptions(1, "profile-bootstrap-reviewed", "control-local", "control-dropbox"),
                new PilotRunControl(), expectedOperationKinds: wrongDirection, onInventory: step =>
                {
                    rejectedProgress.Add(step);
                    return Task.CompletedTask;
                }, admission: admission)
            .RunReviewedAsync("Reviewed first sync", reviewed, "bootstrap-wrong-direction",
                _ => Task.CompletedTask);
        True(rejected.Blocked == 1 && !remote.Exists("local-only.txt"));
        True(rejectedProgress.Any(step => step.Phase == "Comparing Files and Folders" &&
                step.RelativePath == "local-only.txt") && rejectedProgress.Any(step => step.Phase == "Saving Comparison Results"),
            "An eligible rejected direction retains its actual comparison and terminal save reports without an effect.");

        var expectedDirection = new Dictionary<string, SyncAuthorityOperationKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["local-only.txt"] = SyncAuthorityOperationKind.CreateRemote
        };
        var live = new List<SyncAuthorityLiveAction>();
        await authority.RegisterOperationGroupAsync("bootstrap-correct-direction", "compound", "{}");
        await authority.MarkOperationGroupActiveAsync("bootstrap-correct-direction");
        var applied = await new SyncAuthorityPairRunner(pair, remote, authority,
                "root-bootstrap-reviewed-local", "root-bootstrap-reviewed-dropbox",
                new SyncAuthorityPlannerOptions(1, "profile-bootstrap-reviewed", "control-local", "control-dropbox"),
                new PilotRunControl(), expectedOperationKinds: expectedDirection, admission: admission)
            .RunReviewedAsync("Reviewed first sync", reviewed, "bootstrap-correct-direction",
                _ => Task.CompletedTask, onAction: action =>
                {
                    live.Add(action);
                    return Task.CompletedTask;
                });
        True(applied.Uploaded == 1 && remote.ReadText("local-only.txt") == "reviewed upload");
        True(live.Any(item => item.Action == "Upload" && item.Stage == "Starting") &&
             live.Any(item => item.Action == "Upload" && item.Stage == "Completed"));
    });
}

static async Task BootstrapRuntimeActionPlanContract()
{
    if (!OperatingSystem.IsWindows()) return;
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(Path.Combine(localRoot, "Excluded"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "upload.txt"), "upload through runtime host");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "LICENSE"), "extensionless reviewed upload");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "skip-local.txt"), "leave local unchanged");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "Excluded", "local-hidden.txt"), "excluded local");

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var remote = new FakeSyncRemoteStore();
        remote.Put("download.txt", "download through runtime host");
        remote.PutDirectory("archive.v1");
        remote.Put("skip-dropbox.txt", "leave Dropbox unchanged");
        remote.Put("Excluded/dropbox-hidden.txt", "excluded Dropbox");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var http = new HttpClient(new DropboxRecoveryBrowserContractHandler());
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        var binding = await host.BindPairAsync(new SyncPairBindingRequest(null, "Runtime first sync", localRoot,
            new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 50, PropagateDeletions: true, FilesOnDemandEnabled: false,
            SelectionRules: [new SyncSelectionRule("Excluded", false, "id:excluded")],
            SelectionDefaultIncluded: true, RecoveryRetentionDays: 180));
        var pair = binding.Pair;

        var inventoryRun = await host.RunPairsAsync([pair], "Runtime first-sync inventory");
        True(inventoryRun.Items.Single().FailureMessage is null);
        Equal(0, remote.UploadCount);
        Equal(0, remote.DownloadCount);
        var page = await host.LoadBootstrapReviewPageAsync(pair.Id, 100, 0);
        var byPath = page.Items.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        True(page.InventoryComplete &&
             byPath["upload.txt"].Group == "Local only" &&
             byPath["LICENSE"].Group == "Local only" &&
             byPath["download.txt"].Group == "Dropbox only" &&
             byPath["archive.v1"].Group == "Dropbox only" &&
             byPath["skip-local.txt"].Group == "Local only" &&
             byPath["skip-dropbox.txt"].Group == "Dropbox only" &&
             byPath.Keys.All(path => !path.StartsWith("Excluded/", StringComparison.OrdinalIgnoreCase)),
            "The actual runtime inventory did not preserve direction or folder exclusions.");

        var plan = new SyncBootstrapActionPlan(pair.Id, page.LocalScanGenerationId!,
            page.DropboxScanGenerationId!,
            [
                new SyncBootstrapActionChoice("upload.txt", "Local only", "Upload"),
                new SyncBootstrapActionChoice("LICENSE", "Local only", "Upload"),
                new SyncBootstrapActionChoice("download.txt", "Dropbox only", "Download"),
                new SyncBootstrapActionChoice("archive.v1", "Dropbox only", "Download"),
                new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Skip for now"),
                new SyncBootstrapActionChoice("skip-dropbox.txt", "Dropbox only", "Skip for now")
            ]);
        var live = new List<SyncAuthorityLiveAction>();
        var applied = await host.RunBootstrapActionPlanAsync(plan, action =>
        {
            live.Add(action);
            return Task.CompletedTask;
        });
        True(applied.AppliedItems == 4 && applied.SkippedItems == 2 && !applied.FirstSyncComplete);
        Equal("upload through runtime host", remote.ReadText("upload.txt"));
        Equal("extensionless reviewed upload", remote.ReadText("LICENSE"));
        Equal("download through runtime host", await File.ReadAllTextAsync(Path.Combine(localRoot, "download.txt")));
        True(Directory.Exists(Path.Combine(localRoot, "archive.v1")));
        True(!remote.Exists("skip-local.txt") && !File.Exists(Path.Combine(localRoot, "skip-dropbox.txt")));
        True(await File.ReadAllTextAsync(Path.Combine(localRoot, "Excluded", "local-hidden.txt")) == "excluded local" &&
             remote.ReadText("Excluded/dropbox-hidden.txt") == "excluded Dropbox");
        AssertReviewedLifecycle("LICENSE", "Upload", "File", "File");
        AssertReviewedLifecycle("archive.v1", "Download", "Folder", "Folder");

        void AssertReviewedLifecycle(string path, string action, string itemKind, string fileType)
        {
            var pathActions = live.Where(item => string.Equals(item.RelativePath, path,
                StringComparison.Ordinal)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "reviewed_first_sync_lifecycle",
                path,
                events = pathActions.Select(item => new
                    { item.Stage, item.OperationId, item.Action, item.ItemKind, item.FileType, item.Phase })
            }));
            // Detailed byte/prerequisite progress now belongs between the same
            // authoritative start/outcome pair. It must not be mistaken for an
            // extra lifecycle or discarded to satisfy the old two-event fixture.
            True(pathActions.Length >= 2 && pathActions[0].Stage == "Starting" &&
                 pathActions[^1].Stage == "Completed" &&
                 pathActions.Count(item => item.Stage == "Starting") == 1 &&
                 pathActions.Count(item => item.Stage == "Completed") == 1 &&
                 pathActions.Skip(1).SkipLast(1).All(item => item.Phase == SyncActivityPhase.Running &&
                     !string.IsNullOrWhiteSpace(item.Detail)) &&
                 !string.IsNullOrWhiteSpace(pathActions[0].OperationId) &&
                 pathActions.All(item => item.OperationId == pathActions[0].OperationId &&
                                         item.Action == action && item.ItemKind == itemKind &&
                                         item.FileType == fileType),
                $"The reviewed-first-sync producer did not preserve the ordered authoritative lifecycle for {path}.");
            if (path == "LICENSE")
                True(pathActions.Length > 2,
                    "Reviewed file transfers must retain detailed progress between their single start and outcome.");
        }

        var remaining = await host.LoadBootstrapReviewAsync(pair.Id);
        True(remaining.Any(item => item.RelativePath == "skip-local.txt" && item.Group == "Local only") &&
             remaining.Any(item => item.RelativePath == "skip-dropbox.txt" && item.Group == "Dropbox only"),
            "Skipped rows did not remain unresolved after the real runtime refreshed its inventory.");
        var activity = await host.LoadActivityProjectionPageAsync(100, 0, null, "All", pairId: pair.Id);
        True(activity.Any(item => item.RelativePath == "upload.txt" && item.Outcome == "Uploaded") &&
             activity.Any(item => item.RelativePath == "download.txt" && item.Outcome == "Downloaded") &&
             activity.Any(item => item.RelativePath == "LICENSE" && item.Outcome == "Uploaded" &&
                                  item.ItemKind == "File" && item.FileType == "File" &&
                                  item.OperationId == live.Single(action => action.RelativePath == "LICENSE" &&
                                      action.Stage == "Starting").OperationId) &&
             activity.Any(item => item.RelativePath == "archive.v1" && item.Outcome == "Downloaded" &&
                                  item.ItemKind == "Folder" && item.FileType == "Folder" &&
                                  item.OperationId == live.Single(action => action.RelativePath == "archive.v1" &&
                                      action.Stage == "Starting").OperationId),
            "Completed first-sync effects were not persisted in the real Activity projection.");
        var retainedLicense = await host.LoadActivityProjectionPageAsync(100, 0, "LICENSE", "All actions",
            "Files", pair.Id);
        var retainedArchive = await host.LoadActivityProjectionPageAsync(100, 0, "archive.v1", "All actions",
            "Folders", pair.Id);
        True(retainedLicense.Count > 0 && retainedLicense.All(item =>
                 item.OperationId == live.Single(action => action.RelativePath == "LICENSE" &&
                     action.Stage == "Starting").OperationId && item.RelativePath == "LICENSE") &&
             (await host.LoadActivityProjectionPageAsync(100, 0, "LICENSE", "All actions", "Folders", pair.Id))
                 .Count == 0 && retainedArchive.Count > 0 && retainedArchive.All(item =>
                 item.OperationId == live.Single(action => action.RelativePath == "archive.v1" &&
                     action.Stage == "Starting").OperationId && item.RelativePath == "archive.v1") &&
             (await host.LoadActivityProjectionPageAsync(100, 0, "archive.v1", "All actions", "Files", pair.Id))
                 .Count == 0,
            "The reviewed-first-sync retained Activity filters did not preserve the counterexample item kinds.");

        var effectsBeforeRejections = (remote.UploadCount, remote.DownloadCount, remote.DeleteCount,
            remote.MoveCount);
        var remainingPage = await host.LoadBootstrapReviewPageAsync(pair.Id, 100, 0);
        var currentPlan = new SyncBootstrapActionPlan(pair.Id, remainingPage.LocalScanGenerationId!,
            remainingPage.DropboxScanGenerationId!,
            [
                new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Skip for now"),
                new SyncBootstrapActionChoice("skip-dropbox.txt", "Dropbox only", "Skip for now")
            ]);
        var wrongDirection = currentPlan with
        {
            Choices =
            [
                new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Download"),
                new SyncBootstrapActionChoice("skip-dropbox.txt", "Dropbox only", "Skip for now")
            ]
        };
        await ThrowsAsync<InvalidDataException>(() => host.RunBootstrapActionPlanAsync(wrongDirection));
        Equal(effectsBeforeRejections, (remote.UploadCount, remote.DownloadCount, remote.DeleteCount,
            remote.MoveCount));

        var excludedInjection = currentPlan with
        {
            Choices =
            [
                new SyncBootstrapActionChoice("skip-local.txt", "Local only", "Skip for now"),
                new SyncBootstrapActionChoice("skip-dropbox.txt", "Dropbox only", "Skip for now"),
                new SyncBootstrapActionChoice("Excluded/local-hidden.txt", "Local only", "Upload")
            ]
        };
        await ThrowsAsync<InvalidDataException>(() => host.RunBootstrapActionPlanAsync(excludedInjection));
        Equal(effectsBeforeRejections, (remote.UploadCount, remote.DownloadCount, remote.DeleteCount,
            remote.MoveCount));

        var retainedPlan = currentPlan;
        await File.WriteAllTextAsync(Path.Combine(localRoot, "arrived-after-review.txt"), "stale review probe");
        await host.RefreshBootstrapInventoryAsync(pair.Id);
        // A later inventory is not authority to abandon already reviewed choices
        // or auto-approve a new arrival. Both original choices are skips here;
        // apply them without effects and keep the unreviewed arrival outstanding.
        var retainedApply = await host.RunBootstrapActionPlanAsync(retainedPlan);
        True(retainedApply.AppliedItems == 0 && retainedApply.SkippedItems == 2 &&
             !retainedApply.FirstSyncComplete && !remote.Exists("arrived-after-review.txt") &&
             await File.ReadAllTextAsync(Path.Combine(localRoot, "arrived-after-review.txt")) == "stale review probe",
            "Refreshing inventory must preserve reviewed choices without applying or hiding unreviewed arrivals.");
        var afterRetainedApply = await host.LoadBootstrapReviewAsync(pair.Id);
        True(afterRetainedApply.Any(item => item.RelativePath == "arrived-after-review.txt" &&
                                           item.Group == "Local only"),
            "The unreviewed new file remains visible for a later explicit decision.");
        Equal(effectsBeforeRejections, (remote.UploadCount, remote.DownloadCount, remote.DeleteCount,
            remote.MoveCount));

        var localBeforeRetirement = Directory.GetFiles(localRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(localRoot, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var remoteBeforeRetirement = new[] { "upload.txt", "download.txt", "skip-dropbox.txt",
            "Excluded/dropbox-hidden.txt" }.Where(remote.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        async Task<List<SyncAuthorityActivityProjection>> CompleteFixtureActivity()
        {
            var history = new List<SyncAuthorityActivityProjection>();
            for (var offset = 0;;)
            {
                var historyPage = await host.LoadActivityProjectionPageAsync(100, offset, null, "All", pairId: pair.Id);
                history.AddRange(historyPage);
                if (historyPage.Count < 100) return history;
                offset = checked(offset + historyPage.Count);
                True(offset <= 10000, "The bounded retirement fixture unexpectedly produced over 10,000 history rows.");
            }
        }
        // Detailed preparation/progress can push earlier completed transfers off
        // the first page. Compare all retained records, not only the newest 100.
        var activityBeforeRetirement = await CompleteFixtureActivity();
        True(activityBeforeRetirement.Any(item => item.RelativePath == "upload.txt" && item.Outcome == "Uploaded") &&
             activityBeforeRetirement.Any(item => item.RelativePath == "download.txt" && item.Outcome == "Downloaded"),
            "The retirement fixture must contain the original transfer history before deleting the pair.");
        await host.RetirePairAsync(pair.Id);
        var retainedActivity = await CompleteFixtureActivity();
        True(activityBeforeRetirement.All(before => retainedActivity.Any(after =>
                 after.EventId == before.EventId && after.PairId == before.PairId &&
                 after.OperationId == before.OperationId && after.RelativePath == before.RelativePath &&
                 after.Outcome == before.Outcome && after.Reason == before.Reason)),
            "Deleting the pair changed or removed an original retained activity record.");
        True(retainedActivity.Any(item => item.RelativePath == "upload.txt" && item.Outcome == "Uploaded") &&
             retainedActivity.Any(item => item.RelativePath == "download.txt" && item.Outcome == "Downloaded"),
            "Deleting the pair removed its persisted Activity history.");
        Equal(string.Join('|', localBeforeRetirement), string.Join('|', Directory.GetFiles(localRoot, "*",
            SearchOption.AllDirectories).Select(path => Path.GetRelativePath(localRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)));
        Equal(string.Join('|', remoteBeforeRetirement), string.Join('|', remoteBeforeRetirement.Where(remote.Exists)));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            caseId = "TC-SS-RUNTIME-FIRST-SYNC-REAL-PATH",
            applied = applied.AppliedItems,
            skipped = applied.SkippedItems,
            retainedUnresolved = remaining.Count(item => item.Group is "Local only" or "Dropbox only"),
            activityBeforeRetirement = activityBeforeRetirement.Count,
            persistedActivity = retainedActivity.Count,
            rejectedDirections = 1,
            rejectedExcludedPaths = 1,
            retainedReviewWithUnapprovedArrival = 1
        }));
    });

    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "uncertain-after-effect.txt"),
            "provider accepted this once");
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(Binding().Normalize());
        var remote = new FakeSyncRemoteStore();
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var http = new HttpClient(new DropboxRecoveryBrowserContractHandler());
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        var pair = (await host.BindPairAsync(new SyncPairBindingRequest(null, "Uncertain first sync", localRoot,
            new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
            Enabled: true, Priority: 0, PropagateDeletions: true))).Pair;
        True((await host.RunPairsAsync([pair], "Uncertain first-sync inventory")).Items.Single()
            .FailureMessage is null);
        var page = await host.LoadBootstrapReviewPageAsync(pair.Id, 20, 0);
        remote.InterruptNextUploadAfterEffect = true;
        var plan = new SyncBootstrapActionPlan(pair.Id,
            page.LocalScanGenerationId!, page.DropboxScanGenerationId!,
            [new SyncBootstrapActionChoice("uncertain-after-effect.txt", "Local only", "Upload")]);
        var outcome = await host.RunBootstrapActionPlanAsync(plan);
        // A per-file unknown result is returned and retained, not replaced by a whole-run exception.
        // Fail closed means no claimed completion, no repeated effect and explicit verification required.
        True(remote.Exists("uncertain-after-effect.txt") && remote.UploadCount == 1 &&
             !outcome.FirstSyncComplete && outcome.AppliedItems == 0 &&
             outcome.Result.NeedsVerification == 1 && outcome.Result.Decisions.Any(item =>
                 item.RelativePath == "uncertain-after-effect.txt" &&
                 item.Kind == SyncDecisionKind.ResponseUnknown && !string.IsNullOrWhiteSpace(item.Reason)),
            "A provider interruption after its effect must retain an exact per-file unknown outcome after one remote effect.");
        var activity = await host.LoadActivityProjectionPageAsync(100, 0, null, "All", pairId: pair.Id);
        True(activity.Any(item => item.RelativePath == "uncertain-after-effect.txt" &&
                                  item.Outcome == "Effect unknown" &&
                                  item.Reason.Contains("Effect unknown", StringComparison.OrdinalIgnoreCase)),
            "The actual Activity database did not retain the failure-after-effect classification.");
        var remaining = await host.LoadBootstrapReviewAsync(pair.Id);
        True(remaining.Any(item => item.RelativePath == "uncertain-after-effect.txt"),
            "The uncertain first-sync item did not remain visible for explicit follow-up.");
        var problem = (await host.LoadRetainedFirstSyncProblemsAsync()).Single(item => item.PairId == pair.Id);
        True(problem.Phase == "verification" && problem.RelativePath == "uncertain-after-effect.txt" &&
             problem.NextAction.Contains("Verify outcomes", StringComparison.Ordinal) &&
             (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id).InitialReviewApproved == false,
            "The uncertain effect must expose its exact file and verification action without approving first sync.");
        await ThrowsAsync<InvalidOperationException>(() => host.RunBootstrapActionPlanAsync(plan));
        Equal(1, remote.UploadCount);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            caseId = "TC-SS-RUNTIME-FIRST-SYNC-AFTER-EFFECT",
            providerEffectCount = remote.UploadCount,
            needsVerification = outcome.Result.NeedsVerification,
            repeatedApprovalRejected = true,
            retainedActivity = activity.Count,
            retainedReviewRows = remaining.Count
        }));
    });
}

static async Task ConflictProjectionPagingAndGrouping()
{
    await WithTempRoot(async root =>
    {
        const string pairId = "pair-conflict-scale";
        const string localRootId = "root-conflict-scale-local";
        const string dropboxRootId = "root-conflict-scale-dropbox";
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-conflict-scale", "profile-conflict-scale",
            "conflict-scale-test", "{}");
        await authority.RegisterPairAsync(pairId, 1);
        await authority.RegisterRootBindingAsync(localRootId, pairId, "local", 1,
            "{\"volumeSerial\":\"00000002\",\"fileId\":\"0000000000000002\"}", root);
        await authority.RegisterRootBindingAsync(dropboxRootId, pairId, "dropbox", 1,
            "{\"folderId\":\"id:conflict-scale\"}", "/Conflict Scale");
        var localScan = await authority.BeginScanGenerationAsync(pairId, localRootId, "local", "initial",
            "{\"scope\":\"complete\"}");
        var dropboxScan = await authority.BeginScanGenerationAsync(pairId, dropboxRootId, "dropbox", "initial",
            "{\"scope\":\"complete\"}");
        var localEntries = new List<SyncAuthorityObservationInput>();
        var dropboxEntries = new List<SyncAuthorityObservationInput>();
        var baseline = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var localHash = new string('1', 64);
        var dropboxHash = new string('2', 64);
        for (var index = 0; index < 520; index++)
        {
            var path = $"Conflict/item-{index:D4}.txt";
            DateTimeOffset? localModified = baseline;
            DateTimeOffset? dropboxModified = baseline;
            if (index < 210) localModified = baseline.AddHours(3);
            else if (index < 370) dropboxModified = baseline.AddHours(3);
            else if (index >= 470) dropboxModified = null;
            var identity = JsonSerializer.Serialize(new
            {
                volumeSerial = "00000002",
                fileId = index.ToString("x16"),
                lastWriteUtc = localModified?.ToString("O")
            });
            localEntries.Add(new SyncAuthorityObservationInput(path, "file", identity, localHash, index + 1,
                JsonSerializer.Serialize(new
                {
                    path,
                    kind = "file",
                    identity,
                    ContentHash = localHash,
                    Size = index + 1
                })));
            dropboxEntries.Add(new SyncAuthorityObservationInput(path, "file",
                JsonSerializer.Serialize(new { fileId = "id:" + index, revision = "rev:" + index }),
                dropboxHash, index + 1,
                JsonSerializer.Serialize(new
                {
                    path,
                    kind = "file",
                    ContentHash = dropboxHash,
                    Size = index + 1,
                    modifiedUtc = dropboxModified?.ToString("O")
                })));
        }
        await authority.AppendObservationEntriesAsync(localScan, localEntries);
        await authority.AppendObservationEntriesAsync(dropboxScan, dropboxEntries);
        await authority.CompleteScanGenerationAsync(localScan);
        await authority.CompleteScanGenerationAsync(dropboxScan);
        var decisions = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pairId);
        Equal(520, decisions.Count(item => item.Kind == SyncDecisionKind.Conflict));

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "product-state", "instance.lock"));
        using var http = new HttpClient();
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            new DropboxConnectionStore(appLock.StateAuthority),
            new MemoryDropboxRefreshTokenStore("conflict-projection-token"), authority);
        var timer = Stopwatch.StartNew();
        var first = await host.LoadOpenConflictComparisonsPageAsync(200, 0, null, "Local newer");
        var second = await host.LoadOpenConflictComparisonsPageAsync(200, 200, null, "Local newer");
        var selection = await host.CaptureOpenConflictSelectionAsync(null, "Local newer");
        timer.Stop();
        Equal(200, first.Count);
        Equal(10, second.Count);
        Equal(210, selection.Count);
        Equal(210, await host.CountOpenConflictComparisonsAsync(null, "Local newer"));
        Equal(520, await host.CountOpenConflictComparisonsAsync(null));
        True(first.All(item => item.Group == "Local newer" && item.Recommendation == "Review the newer version"));
        True(first.Select(item => item.ConflictId).Intersect(second.Select(item => item.ConflictId)).Count() == 0);
        Equal(1, (await host.CaptureOpenConflictSelectionAsync("item-0519", "Unreliable or missing time")).Count);
        // W2-A: the recommendation is provider-neutral in BOTH timestamp directions and for the near-equal
        // and missing-time cases, and it never names a provider or a side and never auto-selects a winner.
        var newerDropboxPort = await host.LoadOpenConflictComparisonsPageAsync(200, 0, null, "Dropbox newer");
        var nearEqual = await host.LoadOpenConflictComparisonsPageAsync(200, 0, null, "Approximately equal time");
        var missingTime = await host.LoadOpenConflictComparisonsPageAsync(200, 0, null, "Unreliable or missing time");
        True(newerDropboxPort.Count == 160 && newerDropboxPort.All(item =>
                 item.Group == "Dropbox newer" && item.Recommendation == "Review the newer version"),
            "The other timestamp direction keeps the provider-neutral newer-version recommendation.");
        True(nearEqual.Count == 100 && nearEqual.All(item =>
                 item.Group == "Approximately equal time" && item.Recommendation == "Review both versions"),
            "A near-equal pair keeps the review-both recommendation.");
        True(missingTime.Count == 50 && missingTime.All(item =>
                 item.Group == "Unreliable or missing time" && item.Recommendation == "Review both versions"),
            "A missing-time pair keeps the review-both recommendation.");
        True(first.Concat(newerDropboxPort).Concat(nearEqual).Concat(missingTime).All(item =>
                 !item.Recommendation.Contains("local", StringComparison.OrdinalIgnoreCase) &&
                 !item.Recommendation.Contains("dropbox", StringComparison.OrdinalIgnoreCase) &&
                 !item.Recommendation.Contains("google", StringComparison.OrdinalIgnoreCase)),
            "No conflict recommendation names a provider or a physical side.");
        // W2-B: the neutral visual category matches BOTH retained compatibility keys without changing any
        // stored key, and every raw key stays exactly addressable.
        Equal(370, await host.CountOpenConflictComparisonsAsync(null, SyncAuthorityRuntimeHost.NewerVersionGroup));
        Equal(210, await host.CountOpenConflictComparisonsAsync(null, "Local newer"));
        Equal(160, await host.CountOpenConflictComparisonsAsync(null, "Dropbox newer"));
        True((await host.LoadOpenConflictComparisonsPageAsync(400, 0, null, SyncAuthorityRuntimeHost.NewerVersionGroup))
                 .All(item => item.Group is "Local newer" or "Dropbox newer"),
            "The neutral newer-version category contains only the two retained newer keys.");
        // The CLI human projection is the same one shared helper for the conflict list, the conflict detail
        // and the first-sync plan, so no human CLI surface prints a compatibility port as a provider claim
        // while the raw keys stay addressable and the JSON contract keeps them verbatim.
        True(SyncCliRegistry.ConflictGroupLabel("Local newer") == SyncAuthorityRuntimeHost.NewerVersionGroup &&
             SyncCliRegistry.ConflictGroupLabel("Dropbox newer") == SyncAuthorityRuntimeHost.NewerVersionGroup &&
             SyncCliRegistry.ConflictGroupLabel("Approximately equal time") == "Approximately equal time" &&
             SyncCliRegistry.ConflictGroupLabel("Unreliable or missing time") == "Unreliable or missing time",
            "The CLI human conflict group projection is neutral and leaves every other group unchanged.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            caseId = "TC-SS-CONFLICT-PROJECTION-W2",
            localNewerPort = await host.CountOpenConflictComparisonsAsync(null, "Local newer"),
            dropboxPortNewer = await host.CountOpenConflictComparisonsAsync(null, "Dropbox newer"),
            neutralNewerVersion = await host.CountOpenConflictComparisonsAsync(null,
                SyncAuthorityRuntimeHost.NewerVersionGroup),
            approximatelyEqual = await host.CountOpenConflictComparisonsAsync(null, "Approximately equal time"),
            missingTime = await host.CountOpenConflictComparisonsAsync(null, "Unreliable or missing time"),
            recommendations = new[] { "Review the newer version", "Review both versions" }
        }));
        True(timer.Elapsed < TimeSpan.FromSeconds(5));
    });
}

static async Task ConflictSelectionGenerationFence()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "shared.txt"), "local version");
        var remote = new FakeSyncRemoteStore();
        remote.Put("shared.txt", "dropbox version");
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-conflict-fence", "Conflict fence", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Conflict Fence", "id:conflict-fence");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-conflict-fence", "profile-conflict-fence",
            "conflict-fence-test", "{}");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-conflict-fence-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-conflict-fence-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:conflict-fence\"}", pair.DropboxRoot);
        await new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-conflict-fence-local", "root-conflict-fence-dropbox").ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        var conflictId = (await authority.LoadOpenConflictIdsAsync(pair.Id)).Single();
        var conflict = await authority.LoadConflictAsync(conflictId);
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var runner = new SyncAuthorityPairRunner(pair, remote, authority,
            "root-conflict-fence-local", "root-conflict-fence-dropbox",
            new SyncAuthorityPlannerOptions(1, "profile-conflict-fence", "control-local", "control-dropbox"),
            new PilotRunControl(), admission: admission);
        await ThrowsAsync<InvalidOperationException>(() => runner.ResolveConflictAsync("shared.txt",
            SyncAuthorityConflictResolutionKind.KeepLocal, "conflict-stale-selection",
            conflict.ConflictGeneration));
        Equal("dropbox version", remote.ReadText("shared.txt"));
        Equal(conflictId, (await authority.LoadOpenConflictIdsAsync(pair.Id)).Single());
    });
}

static async Task AppendOnlyState()
{
    await WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var path = Path.Combine(appLock.StateAuthority.StateRoot, "state.jsonl");
        var store = new AppendOnlyJsonStore<string>(appLock.StateAuthority, "state.jsonl");
        await store.AppendSetAsync("first", default);
        await store.AppendSetAsync("second", default);
        Equal("second", store.Load());
        await File.AppendAllTextAsync(path, "{torn");
        Equal("second", store.Load());
        var tornHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(appLock.StateAuthority.StateRoot,
            "state.jsonl.recovery-" + tornHash + ".json"), "partial-evidence");
        await store.AppendDeleteAsync(default);
        Equal<string?>(null, store.Load());
        var evidencePath = Directory.GetFiles(appLock.StateAuthority.StateRoot, "state.jsonl.recovery-*.json").Single();
        using (var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath)))
            Equal("{torn", Encoding.UTF8.GetString(Convert.FromBase64String(evidence.RootElement.GetProperty("BadBytesBase64").GetString()!)));

        var corruptPath = Path.Combine(appLock.StateAuthority.StateRoot, "corrupt-state.jsonl");
        await File.WriteAllTextAsync(corruptPath, "not-json\n");
        var corruptStore = new AppendOnlyJsonStore<string>(appLock.StateAuthority, "corrupt-state.jsonl");
        Equal<string?>(null, corruptStore.Load());
        await corruptStore.AppendSetAsync("recovered", default);
        Equal("recovered", corruptStore.Load());
        Equal(1, Directory.GetFiles(appLock.StateAuthority.StateRoot, "corrupt-state.jsonl.recovery-*.json").Length);

        var cleanStore = new AppendOnlyJsonStore<string>(appLock.StateAuthority, "clean-state.jsonl");
        await cleanStore.AppendSetAsync("present", default);
        await cleanStore.AppendDeleteAsync(default);
        Equal<string?>(null, cleanStore.Load());
    });
}

static async Task RecoveryCeilingReserve()
{
    await WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var stateRoot = appLock.StateAuthority.StateRoot;
        var legacyLine = AppendOnlyJsonStore<string>.SerializeSetLine("legacy");
        var completeLine = legacyLine.Concat(new[] { (byte)'\n' }).ToArray();

        var ninetyNineName = "legacy-99.jsonl";
        var ninetyNinePath = Path.Combine(stateRoot, ninetyNineName);
        await File.WriteAllBytesAsync(ninetyNinePath,
            Enumerable.Repeat(completeLine, 99).SelectMany(value => value)
                .Concat(legacyLine.Take(legacyLine.Length / 2)).ToArray());
        var ninetyNineStore = new AppendOnlyJsonStore<string>(appLock.StateAuthority, ninetyNineName);
        await ninetyNineStore.AppendSetAsync("recovered-99", default);
        Equal("recovered-99", ninetyNineStore.Load());
        Equal(AppendOnlyJsonStore<string>.RecoveryRecordReserve, File.ReadLines(ninetyNinePath).Count());

        var hundredName = "legacy-100.jsonl";
        var hundredPath = Path.Combine(stateRoot, hundredName);
        await File.WriteAllBytesAsync(hundredPath,
            Enumerable.Repeat(completeLine, AppendOnlyJsonStore<string>.PhysicalMaximumRecords)
                .SelectMany(value => value).Concat(Bytes("{torn-tail")).ToArray());
        var hundredStore = new AppendOnlyJsonStore<string>(appLock.StateAuthority, hundredName);
        await hundredStore.AppendDeleteAsync(default);
        Equal<string?>(null, hundredStore.Load());
        Equal(AppendOnlyJsonStore<string>.RecoveryRecordReserve, File.ReadLines(hundredPath).Count());

        var byteName = "legacy-byte-boundary.jsonl";
        const string recoveredValue = "recovered-at-byte-boundary";
        var byteReserve = AppendOnlyJsonStore<string>.RecoveryByteReserveForSet(byteName, recoveredValue);
        var validPrefixLength = AppendOnlyJsonStore<string>.PhysicalMaximumBytes - byteReserve;
        var emptyLineLength = AppendOnlyJsonStore<string>.SerializeSetLine(string.Empty).Length;
        var paddedValue = new string('x', validPrefixLength - emptyLineLength - 1);
        var boundaryLine = AppendOnlyJsonStore<string>.SerializeSetLine(paddedValue);
        Equal(validPrefixLength - 1, boundaryLine.Length);
        var bytePath = Path.Combine(stateRoot, byteName);
        await File.WriteAllBytesAsync(bytePath, boundaryLine.Concat(new[] { (byte)'\n' }).ToArray());
        var byteStore = new AppendOnlyJsonStore<string>(appLock.StateAuthority, byteName);
        var beforeRejectedAppend = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(bytePath)));
        await ThrowsAsync<IOException>(() => byteStore.AppendSetAsync(recoveredValue, default));
        Equal(beforeRejectedAppend, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(bytePath))));
        await File.AppendAllTextAsync(bytePath, "{");
        await byteStore.AppendSetAsync(recoveredValue, default);
        Equal(recoveredValue, byteStore.Load());
        True(new FileInfo(bytePath).Length <= byteReserve);
        Equal(AppendOnlyJsonStore<string>.RecoveryRecordReserve, File.ReadLines(bytePath).Count());
    });
}

static Task InstanceLockExclusive()
{
    return WithTempRoot(root =>
    {
        var path = Path.Combine(root, "state", "instance.lock");
        using var first = PilotInstanceLock.Acquire(path);
        Throws<IOException>(() => PilotInstanceLock.Acquire(path));
        Throws<IOException>(() => Directory.Move(first.StateAuthority.StateRoot, first.StateAuthority.StateRoot + ".moved"));
        return Task.CompletedTask;
    });
}

static async Task StateIdentitySubstitution()
{
    await WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new AppendOnlyJsonStore<string>(appLock.StateAuthority, "identity.jsonl");
        await store.AppendSetAsync("bound", default);
        var path = Path.Combine(appLock.StateAuthority.StateRoot, "identity.jsonl");
        File.Move(path, path + ".displaced");
        await File.WriteAllTextAsync(path, string.Empty);
        Throws<IOException>(() => store.Load());
    });
}

static async Task MutableStateHardLinks()
{
    await WithTempRoot(async root =>
    {
        var lockStateRoot = Path.Combine(root, "lock-role", "state");
        Directory.CreateDirectory(lockStateRoot);
        var foreignLock = Path.Combine(root, "foreign-lock.txt");
        await File.WriteAllTextAsync(foreignLock, "foreign-lock-bytes");
        NativeTestFiles.CreateHardLink(Path.Combine(lockStateRoot, "instance.lock"), foreignLock);
        Throws<IOException>(() => PilotInstanceLock.Acquire(Path.Combine(lockStateRoot, "instance.lock")));
        Equal("foreign-lock-bytes", await File.ReadAllTextAsync(foreignLock));

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "product", "state", "instance.lock"));
        var stateRoot = appLock.StateAuthority.StateRoot;

        async Task RejectRole(string role, string fileName, Func<Task> mutate)
        {
            var foreign = Path.Combine(root, "foreign-" + role + ".txt");
            var foreignBytes = "foreign-" + role + "-bytes";
            await File.WriteAllTextAsync(foreign, foreignBytes);
            NativeTestFiles.CreateHardLink(Path.Combine(stateRoot, fileName), foreign);
            await ThrowsAsync<IOException>(mutate);
            Equal(foreignBytes, await File.ReadAllTextAsync(foreign));
        }

        await RejectRole("settings", SyncSharkRuntimePaths.SettingsFileName, () =>
            new PilotSettingsStore(appLock.StateAuthority).SaveAsync(
                new PilotSettings(Path.Combine(root, "local"), 1, 1, "/Pilot", "id:pilot-folder")));
        await RejectRole("connection", SyncSharkRuntimePaths.ConnectionBindingFileName, () =>
            new DropboxConnectionStore(appLock.StateAuthority).SaveNewAsync(Binding()));
        await RejectRole("journal", "journal.jsonl", () =>
            new AppendOnlyJsonStore<string>(appLock.StateAuthority, "journal.jsonl").AppendSetAsync("mutate", default));

        var recoverySourceName = "recovery-source.jsonl";
        var recoverySourcePath = Path.Combine(stateRoot, recoverySourceName);
        var valid = AppendOnlyJsonStore<string>.SerializeSetLine("before-recovery")
            .Concat(new[] { (byte)'\n' }).Concat(Bytes("{torn")).ToArray();
        await File.WriteAllBytesAsync(recoverySourcePath, valid);
        var originalSha = Convert.ToHexString(SHA256.HashData(valid)).ToLowerInvariant();
        var evidenceName = recoverySourceName + ".recovery-" + originalSha + ".json";
        var foreignEvidence = Path.Combine(root, "foreign-recovery-evidence.txt");
        await File.WriteAllTextAsync(foreignEvidence, "foreign-recovery-evidence-bytes");
        NativeTestFiles.CreateHardLink(Path.Combine(stateRoot, evidenceName), foreignEvidence);
        var sourceBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(recoverySourcePath)));
        await ThrowsAsync<IOException>(() =>
            new AppendOnlyJsonStore<string>(appLock.StateAuthority, recoverySourceName).AppendSetAsync("after-recovery", default));
        Equal("foreign-recovery-evidence-bytes", await File.ReadAllTextAsync(foreignEvidence));
        Equal(sourceBefore, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(recoverySourcePath))));
    });
}

static async Task DropboxUploadWireContract()
{
    var handler = new DropboxContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    await client.VerifyRootIdentityAsync();
    await using var source = new MemoryStream(Bytes("wire bytes"));
    var hash = await DropboxContentHash.ComputeAsync(source);
    source.Position = 0;
    var result = await client.UploadCreateOnlyAsync("wire.txt", source, hash);
    Equal("add", handler.UploadMode);
    Equal("id:pilot-folder/wire.txt", handler.UploadPath);
    Equal(false, handler.UploadAutorename);
    Equal(true, handler.UploadStrictConflict);
    Equal("root", handler.PathRootTag);
    Equal("root-namespace", handler.PathRootNamespace);
    Equal(hash, result.ContentHash);
}

static async Task ExactFileUploadContract() =>
    await WithTempRoot(async root => await ExactFileUploadChecks.RunAsync(Path.Combine(root, "exact-file-upload")));

static async Task EmptyFolderBinding()
{
    var handler = new DropboxContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding());
    var result = await client.BindEmptyRootAsync();
    Equal("/Pilot", result.Path);
    Equal("id:pilot-folder", result.FolderId);
    Equal(2, handler.PathMetadataRequests);
}

static async Task DropboxFolderBrowserContract()
{
    var handler = new DropboxBrowserContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxBrowserClient(httpClient, new FixedTokenSource(), Binding());
    var first = await client.ListFoldersAsync();
    Equal(1, first.Folders.Count);
    Equal("Alpha", first.Folders[0].Name);
    Equal("id:alpha", first.Folders[0].FolderId);
    Equal("cursor-1", first.ContinueCursor);
    var second = await client.ContinueFoldersAsync(first.ContinueCursor!);
    Equal(1, second.Folders.Count);
    Equal("Beta", second.Folders[0].Name);
    Equal<string?>(null, second.ContinueCursor);
    var created = await client.CreateFolderAsync("id:alpha", "Child");
    Equal("id:alpha/Child", handler.CreatedPath);
    Equal("id:child", created.FolderId);
    Equal(true, handler.CreateReadbackCalled);
    Equal("root", handler.PathRootTag);
    Equal("root-namespace", handler.PathRootNamespace);
    Equal(true, handler.IncludeMountedFolders);
    Equal(200, handler.ListLimit);
    await client.ListFoldersAsync(PilotPath.DropboxNamespaceRootIdentity("root-namespace"));
    Equal(string.Empty, handler.LastListPath);
    var searched = await client.SearchFoldersAsync("reports", "id:alpha");
    Equal(1, searched.Folders.Count);
    Equal("Reports", searched.Folders[0].Name);
    Equal("search-cursor-1", searched.ContinueCursor);
    Equal("reports", handler.SearchQuery);
    Equal("id:alpha", handler.SearchPath);
    Equal(200, handler.SearchLimit);
    var searchContinuation = await client.ContinueFolderSearchAsync(searched.ContinueCursor!);
    Equal(1, searchContinuation.Folders.Count);
    Equal("Reports Archive", searchContinuation.Folders[0].Name);
    Equal<string?>(null, searchContinuation.ContinueCursor);
    Equal(true, handler.SearchContinueCalled);
    await ThrowsAsync<ArgumentException>(() => client.CreateFolderAsync("/Alpha", "../bad"));

    using var rejectedHttp = new HttpClient(new DropboxBrowserContractHandler(nonFolderCreate: true));
    var rejected = new DropboxBrowserClient(rejectedHttp, new FixedTokenSource(), Binding());
    await ThrowsAsync<InvalidDataException>(() => rejected.CreateFolderAsync(null, "Wrong"));

    using var mismatchedHttp = new HttpClient(new DropboxBrowserContractHandler(mismatchedReadback: true));
    var mismatched = new DropboxBrowserClient(mismatchedHttp, new FixedTokenSource(), Binding());
    await ThrowsAsync<InvalidDataException>(() => mismatched.CreateFolderAsync("id:alpha", "Child"));
}

static async Task DropboxFolderBrowserPagination()
{
    var cumulativeHandler = new DropboxBrowserCeilingHandler();
    using (var httpClient = new HttpClient(cumulativeHandler))
    {
        var client = new DropboxBrowserClient(httpClient, new FixedTokenSource(), Binding());
        var observed = 0;
        await foreach (var _ in client.EnumerateFoldersAsync()) observed++;
        Equal(22_000, observed);
        Equal(10, cumulativeHandler.ContinueCalls);
    }

    using var oversizedClient = new HttpClient(new DropboxBrowserOversizedHandler());
    var oversizedBrowser = new DropboxBrowserClient(oversizedClient, new FixedTokenSource(), Binding());
    await ThrowsAsync<InvalidDataException>(() => oversizedBrowser.ListFoldersAsync());

    foreach (var mode in new[] { "chunked-oversized", "terminal-overflow", "blank-cursor", "oversized-cursor" })
    {
        using var adverseHttp = new HttpClient(new DropboxBrowserAdverseHandler(mode));
        var adverse = new DropboxBrowserClient(adverseHttp, new FixedTokenSource(), Binding());
        await ThrowsAsync<InvalidDataException>(() => adverse.ListFoldersAsync());
    }
    foreach (var mode in new[] { "malformed", "truncated" })
    {
        using var adverseHttp = new HttpClient(new DropboxBrowserAdverseHandler(mode));
        var adverse = new DropboxBrowserClient(adverseHttp, new FixedTokenSource(), Binding());
        await ThrowsAsync<JsonException>(() => adverse.ListFoldersAsync());
    }
    using (var repeatedHttp = new HttpClient(new DropboxBrowserAdverseHandler("repeated-cursor")))
    {
        var repeated = new DropboxBrowserClient(repeatedHttp, new FixedTokenSource(), Binding());
        await ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in repeated.EnumerateFoldersAsync()) { }
        });
    }
    using (var cyclingHttp = new HttpClient(new DropboxBrowserAdverseHandler("cursor-cycle")))
    {
        var cycling = new DropboxBrowserClient(cyclingHttp, new FixedTokenSource(), Binding());
        await ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in cycling.EnumerateFoldersAsync()) { }
        });
    }
    using (var cancelledHttp = new HttpClient(new DropboxBrowserAdverseHandler("cancel")))
    {
        var cancelled = new DropboxBrowserClient(cancelledHttp, new FixedTokenSource(), Binding());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await ThrowsAsync<OperationCanceledException>(() => cancelled.ListFoldersAsync(cancellationToken: cancellation.Token));
    }
}

static async Task NonemptyFolderRejected()
{
    var handler = new DropboxContractHandler(nonempty: true);
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding());
    await ThrowsAsync<InvalidDataException>(() => client.BindEmptyRootAsync());
}

static async Task UncertainUploadTruthful()
{
    await WithTempRoot(async root =>
    {
        Write(root, "upload.txt", "uncertain");
        var remote = new FakeRemoteStore([], throwUncertain: true);
        var result = await new PilotEngine(Settings(root), remote, new PilotRunControl()).RunAsync();
        Equal(0, result.Uploaded);
        Equal(1, result.Blocked);
        Contains("may or may not exist", result.Decisions.Single().Reason);
    });
}

static Task OAuthRequest()
{
    var flow = DropboxOAuth.CreatePkceFlow();
    var uri = DropboxOAuth.BuildAuthorizationUri("fixture-client", new Uri(DropboxProviderConfiguration.RegisteredRedirectUri), flow).AbsoluteUri;
    Contains("code_challenge_method=S256", uri);
    Contains("token_access_type=offline", uri);
    Contains("scope=account_info.read%20files.content.read%20files.content.write%20files.metadata.read%20files.metadata.write%20sharing.read%20sharing.write", uri);
    return Task.CompletedTask;
}

static Task OAuthCallbackValidation()
{
    const string path = "/oauth/dropbox/callback/";
    const string state = "expected-state";
    Equal("authorization-code", DropboxLoopbackReceiver.ParseTarget(path + "?code=authorization-code&state=" + state, path, state));
    Throws<InvalidDataException>(() => DropboxLoopbackReceiver.ParseTarget(path + "?code=authorization-code&state=wrong", path, state));
    Throws<InvalidDataException>(() => DropboxLoopbackReceiver.ParseTarget("/wrong/?code=authorization-code&state=" + state, path, state));
    return Task.CompletedTask;
}

static Task OAuthAcknowledgement()
{
    Contains("Response received", DropboxLoopbackReceiver.AcknowledgementHtml);
    Contains("Return to Sync Shark", DropboxLoopbackReceiver.AcknowledgementHtml);
    Contains("background:#000", DropboxLoopbackReceiver.AcknowledgementHtml);
    True(!DropboxLoopbackReceiver.AcknowledgementHtml.Contains("authorization-code", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static Task UiThemePreference()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new UiPreferencesStore(appLock.StateAuthority);
        Equal(UiPreferencesStore.SystemTheme, store.Load().Theme);
        await store.SaveAsync(new UiPreferences(UiPreferencesStore.AmoledTheme));
        Equal(UiPreferencesStore.AmoledTheme, store.Load().Theme);
        await ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new UiPreferences("Sepia")));
    });
}

static Task BoundedRoots()
{
    Equal("/", PilotPath.NormalizeDropboxRoot("/"));
    True(PilotPath.IsDropboxFolderIdentity("ns:fixture-root"));
    Throws<ArgumentException>(() => PilotPath.NormalizeDropboxRoot("/Pilot/../Other"));
    Throws<ArgumentException>(() => PilotPath.NormalizeTopLevelName("sub/file.txt"));
    var volume = Path.GetPathRoot(Path.GetTempPath())!;
    Throws<ArgumentException>(() => PilotPath.NormalizeLocalRoot(volume));
    Equal(Path.GetFullPath(Path.Combine(volume, "Dropbox", "Pilot")),
        PilotPath.NormalizeLocalRoot(Path.Combine(volume, "Dropbox", "Pilot")));
    Throws<InvalidDataException>(() => PilotPath.RequireSeparatedFromProductState(SyncSharkRuntimePaths.ProductRoot));
    Throws<InvalidDataException>(() => PilotPath.RequireSeparatedFromProductState(SyncSharkRuntimePaths.StateRoot));
    Throws<InvalidDataException>(() => PilotPath.RequireSeparatedFromProductState(Path.GetDirectoryName(SyncSharkRuntimePaths.ProductRoot)!));
    Throws<InvalidDataException>(() => PilotPath.RequireSeparatedFromProductState(Path.Combine(SyncSharkRuntimePaths.StateRoot, "selected")));
    Equal("/Pilot", PilotPath.NormalizeDropboxRoot("Pilot"));
    return Task.CompletedTask;
}

static async Task DropboxUploadHttpFailureUncertain()
{
    var handler = new DropboxContractHandler { UploadStatusCode = HttpStatusCode.InternalServerError };
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    await client.VerifyRootIdentityAsync();
    await using var source = new MemoryStream(Bytes("status unknown"));
    var hash = await DropboxContentHash.ComputeAsync(source);
    source.Position = 0;
    await ThrowsAsync<DropboxEffectUncertainException>(() => client.UploadCreateOnlyAsync("unknown.txt", source, hash));
}

static Task DualSlotRecovery()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new DualSlotJsonStore<string>(appLock.StateAuthority, "alternating-test");
        await store.SaveAsync("one");
        await store.SaveAsync("two");
        Equal("two", store.Load());
        var newest = Path.Combine(appLock.StateAuthority.StateRoot, "alternating-test-b.json");
        await File.WriteAllTextAsync(newest, "{torn");
        Equal("one", store.Load());
        await store.SaveAsync("three");
        Equal("three", store.Load());
    });
}

static Task DurableSyncHistory()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncHistoryStore(appLock.StateAuthority);
        var result = new SyncRunResult("pair-id", [new SyncDecision("file.txt", SyncDecisionKind.Uploaded, "accepted")]);
        for (var index = 0; index < 120; index++) await store.AppendRunAsync("Pair name", result);
        var history = store.Load();
        Equal(120, history.Entries.Count);
        var entry = history.Entries.First();
        Equal("pair-id", entry.PairId);
        Equal("file.txt", entry.RelativePath);
        Equal(SyncDecisionKind.Uploaded, entry.Outcome);
    });
}

static Task AutomationSettings()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncAutomationStore(appLock.StateAuthority);
        Equal(true, store.Load().Enabled);
        await store.SaveAsync(new SyncAutomationSettings(true, 5, true, StartWithWindows: true));
        var enabled = new SyncAutomationStore(appLock.StateAuthority).Load();
        Equal(true, enabled.Enabled);
        Equal(5, enabled.IntervalMinutes);
        Equal(false, enabled.RunOnStartup);
        Equal(true, enabled.StartWithWindows);
        await ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new SyncAutomationSettings(true, 0, false)));
        await store.SaveAsync(new SyncAutomationSettings(false, 15, true, StartWithWindows: true));
        var paused = new SyncAutomationStore(appLock.StateAuthority).Load();
        Equal(false, paused.Enabled);
        Equal(false, paused.RunOnStartup);
        Equal(true, paused.StartWithWindows);
    });
}

static Task SafeExitCheckpointContract()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var httpClient = new HttpClient();
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            new DropboxConnectionStore(appLock.StateAuthority), new MemoryDropboxRefreshTokenStore(), authority);
        var runControl = await host.BeginRunForTestAsync();
        try
        {
            Equal(false, await host.WaitForActiveRunCheckpointAsync(TimeSpan.FromMilliseconds(25)));
            Equal(true, await host.StopActiveRunAsync());
            True(runControl.IsStopRequested);
            Equal(false, await host.WaitForActiveRunCheckpointAsync(TimeSpan.FromMilliseconds(25)));
        }
        finally
        {
            host.EndRunForTest(runControl);
        }
        Equal(true, await host.WaitForActiveRunCheckpointAsync(TimeSpan.FromSeconds(1)));

        // A stale or duplicate completion callback must not over-release the single-run gate.
        host.EndRunForTest(runControl);
        var nextRun = await host.BeginRunForTestAsync();
        try
        {
            await ThrowsAsync<InvalidOperationException>(() => host.BeginRunForTestAsync());
        }
        finally
        {
            host.EndRunForTest(nextRun);
        }
    });
}

static async Task MutableStateLifetimeAndMigration()
{
    await WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        var settings = new PilotSettingsStore(appLock.StateAuthority);
        var connections = new DropboxConnectionStore(appLock.StateAuthority);

        for (var index = 0; index < 180; index++)
        {
            var theme = index % 3 == 0 ? UiPreferencesStore.SystemTheme :
                index % 3 == 1 ? UiPreferencesStore.LightTheme : UiPreferencesStore.AmoledTheme;
            await preferences.SaveAsync(new UiPreferences(theme));
            await settings.SaveAsync(new PilotSettings(Path.Combine(root, "local-" + index), 1, 1,
                "/Pilot-" + index, "id:pilot-" + index));
        }
        Equal(UiPreferencesStore.AmoledTheme, preferences.Load().Theme);
        Equal("/Pilot-179", settings.Load()!.DropboxRoot);

        for (var index = 0; index < 130; index++)
        {
            var binding = Binding() with { DisplayName = "Connection " + index };
            await connections.SaveNewAsync(binding);
            Equal(binding.DisplayName, connections.Load()!.DisplayName);
            await connections.DeleteAsync();
            Equal<DropboxConnectionBinding?>(null, connections.Load());
        }
        await connections.SaveNewAsync(Binding() with { DisplayName = "Final connection" });
        Equal("Final connection", connections.Load()!.DisplayName);

        foreach (var slot in Directory.GetFiles(appLock.StateAuthority.StateRoot, "*.json"))
            True(new FileInfo(slot).Length is > 0 and <= DualSlotJsonStore<string>.MaximumBytes);
        Equal(0, Directory.GetFiles(appLock.StateAuthority.StateRoot, "*.jsonl").Length);
    });

    await WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var stateRoot = appLock.StateAuthority.StateRoot;
        var legacyPreferences = new AppendOnlyJsonStore<UiPreferences>(appLock.StateAuthority,
            SyncSharkRuntimePaths.UiPreferencesFileName);
        await legacyPreferences.AppendSetAsync(new UiPreferences(UiPreferencesStore.LightTheme), default);
        var legacyPreferencePath = Path.Combine(stateRoot, SyncSharkRuntimePaths.UiPreferencesFileName);
        await File.AppendAllTextAsync(legacyPreferencePath, "{torn");
        var legacyPreferenceHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(legacyPreferencePath)));

        var migratedPreferences = new UiPreferencesStore(appLock.StateAuthority);
        Equal(UiPreferencesStore.LightTheme, migratedPreferences.Load().Theme);
        Equal(legacyPreferenceHash, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(legacyPreferencePath))));
        await migratedPreferences.SaveAsync(new UiPreferences(UiPreferencesStore.AmoledTheme));
        await File.WriteAllTextAsync(Path.Combine(stateRoot, "ui-preferences-b.json"), "{torn");
        Equal(UiPreferencesStore.LightTheme, new UiPreferencesStore(appLock.StateAuthority).Load().Theme);
        await migratedPreferences.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme));
        Equal(UiPreferencesStore.SystemTheme, migratedPreferences.Load().Theme);

        var legacyConnection = new AppendOnlyJsonStore<DropboxConnectionBinding>(appLock.StateAuthority,
            SyncSharkRuntimePaths.ConnectionBindingFileName);
        await legacyConnection.AppendSetAsync(Binding() with { DisplayName = "Legacy binding" }, default);
        var legacyConnectionPath = Path.Combine(stateRoot, SyncSharkRuntimePaths.ConnectionBindingFileName);
        var legacyConnectionHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(legacyConnectionPath)));
        var migratedConnection = new DropboxConnectionStore(appLock.StateAuthority);
        Equal("Legacy binding", migratedConnection.Load()!.DisplayName);
        await migratedConnection.DeleteAsync();
        Equal<DropboxConnectionBinding?>(null, new DropboxConnectionStore(appLock.StateAuthority).Load());
        Equal(legacyConnectionHash, Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(legacyConnectionPath))));

        await File.WriteAllTextAsync(Path.Combine(stateRoot, "dropbox-connection-a.json"), "{corrupt-a");
        await File.WriteAllTextAsync(Path.Combine(stateRoot, "dropbox-connection-b.json"), "{corrupt-b");
        Throws<InvalidDataException>(() => new DropboxConnectionStore(appLock.StateAuthority).Load());
    });
}

static async Task RecommendedRecoveryFolderContract()
{
    var browser = new InMemoryDropboxFolderBrowser();
    var first = await SyncAuthorityRuntimeHost.EnsureRecommendedRecoveryFolderCoreAsync(browser, []);
    Equal("/Apps/Sync Shark/Recovery", first.Path);
    Equal("id:folder:3", first.FolderId);
    Equal(3, browser.CreateCount);

    var second = await SyncAuthorityRuntimeHost.EnsureRecommendedRecoveryFolderCoreAsync(browser, []);
    Equal(first, second);
    Equal(3, browser.CreateCount);

    var overlap = new SyncPair("pair-recovery-overlap", "Overlap", @"C:\fixture", 1, 2,
        "/Apps/Sync Shark", "id:overlap");
    var emptyOverlapBrowser = new InMemoryDropboxFolderBrowser();
    await ThrowsAsync<InvalidOperationException>(() =>
        SyncAuthorityRuntimeHost.EnsureRecommendedRecoveryFolderCoreAsync(emptyOverlapBrowser, [overlap]));
    Equal(0, emptyOverlapBrowser.CreateCount);

    for (var ordinal = 1; ordinal <= 3; ordinal++)
    {
        var mismatchedCreate = new InMemoryDropboxFolderBrowser(ordinal);
        await ThrowsAsync<InvalidDataException>(() =>
            SyncAuthorityRuntimeHost.EnsureRecommendedRecoveryFolderCoreAsync(mismatchedCreate, []));
        Equal(ordinal, mismatchedCreate.CreateCount);
    }

    for (var ordinal = 1; ordinal <= 3; ordinal++)
    {
        var mismatchedExisting = new InMemoryDropboxFolderBrowser();
        var parentId = (string?)null;
        var expectedPath = string.Empty;
        var names = new[] { "Apps", "Sync Shark", "Recovery" };
        for (var index = 0; index < ordinal; index++)
        {
            expectedPath += "/" + names[index];
            var path = index + 1 == ordinal ? "/Outside/" + names[index] : expectedPath;
            parentId = mismatchedExisting.SeedFolder(parentId, names[index], path,
                "id:seed:" + (index + 1));
        }
        await ThrowsAsync<InvalidDataException>(() =>
            SyncAuthorityRuntimeHost.EnsureRecommendedRecoveryFolderCoreAsync(mismatchedExisting, []));
        Equal(0, mismatchedExisting.CreateCount);
    }
}

static async Task DropboxArtifactContainmentContract()
{
    Equal("/Apps/Sync Shark", SyncDropboxArtifactPaths.ProductContainerPath);
    Equal("/Apps/Sync Shark/Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath);
    Equal("/Apps/Sync Shark/Testing/run-147", SyncDropboxArtifactPaths.BuildTestRunRoot("run-147"));
    Equal("/Apps/Sync Shark/Recovery/pair-identifier/0123456789abcdef0123456789abcdef",
        SyncDropboxArtifactPaths.BuildRecoveryOperationPath("pair-identifier",
            "0123456789ABCDEF0123456789ABCDEF"));
    Equal(SyncDropboxArtifactPaths.RecoveryContainerPath, SyncControlNamespace.RemoteContainerPath);
    Equal("/Apps/Sync Shark/Testing/run-147/result.json",
        SyncDropboxArtifactPaths.RequireContained("/Apps/Sync Shark/Testing/run-147/result.json"));
    Equal(SyncDropboxArtifactPaths.RecoveryContainerPath,
        SyncDropboxArtifactPaths.RequireRecoveryContainer("/apps/sync shark/recovery/"));

    foreach (var outside in new[]
             {
                 "/Sync Shark Pilot", "/Sync Shark acceptance old", "/Sync Shark recovery old",
                 "/Sync Shark Recovery/pair-identifier/0123456789abcdef0123456789abcdef", "/Apps/Another App",
                 "/Apps/Sync Shark", "/Apps/Sync Shark/"
             })
        Throws<InvalidDataException>(() => SyncDropboxArtifactPaths.RequireContained(outside));

    foreach (var outsideRecovery in new[]
             {
                 "/Recovery", "/Backups/Recovery", "/Apps/Sync Shark",
                 "/Apps/Sync Shark/Recovery/pair-identifier"
             })
        Throws<InvalidDataException>(() => SyncDropboxArtifactPaths.RequireRecoveryContainer(outsideRecovery));

    SyncControlNamespace.RequireRemotePairRootAllowed("/Work");
    SyncControlNamespace.RequireRemotePairRootAllowed("/Sync Shark customer files");
    Throws<InvalidDataException>(() =>
        SyncDropboxArtifactPaths.RequireContained("/Sync Shark customer files"));
    Throws<InvalidDataException>(() =>
        SyncControlNamespace.RequireRemotePairRootAllowed(SyncDropboxArtifactPaths.ProductContainerPath));

    var productLikePair = new SyncPair("pair-product-like", "Product-like user content", @"C:\fixture",
        1, 2, "/Sync Shark customer files", "id:product-like", RecoveryRoot:
        new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
            "id:recovery", Binding().RootNamespaceId));
    Equal("/Sync Shark customer files", SyncPairStore.NormalizePair(productLikePair).DropboxRoot);
    Throws<InvalidDataException>(() => SyncPairStore.NormalizePair(productLikePair with
    {
        RecoveryRoot = productLikePair.RecoveryRoot! with { RootPath = "/Elsewhere" }
    }));

    var network = new NoNetworkHttpHandler();
    using var http = new HttpClient(network);
    var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Work", Binding(), "id:work");
    await ThrowsAsync<InvalidDataException>(() => client.VerifyRecoveryRootAsync(
        new SyncRemoteRecoveryRootBinding("/Elsewhere", "id:elsewhere", Binding().RootNamespaceId)));
    Equal(0, network.CallCount);

    var invalidPair = productLikePair with
    {
        RecoveryRoot = productLikePair.RecoveryRoot! with { RootPath = "/Elsewhere" }
    };
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityUpdateLocalExecutor(invalidPair, null!, null!).ExecuteAsync("operation", "worker"));
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityUpdateLocalReconciler(invalidPair, null!, null!).ReconcileAsync("operation", "worker"));
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityUpdateRemoteExecutor(invalidPair, null!, null!).ExecuteAsync("operation", "worker"));
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityUpdateRemoteReconciler(invalidPair, null!, null!).ReconcileAsync("operation", "worker"));
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityDeleteRemoteExecutor(invalidPair, null!, null!).ExecuteAsync("operation", "worker"));
    await ThrowsAsync<InvalidDataException>(() =>
        new SyncAuthorityDeleteRemoteReconciler(invalidPair, null!, null!).ReconcileAsync("operation", "worker"));

    await RuntimeRecoveryConsumerAdmissionContract(invalidPair);
    await ProductLikePairBindingContract();
}

static async Task PersistedInvalidRecoveryBindingContract()
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var valid = SyncPair.FromPilot(new PilotSettings(localLease.Path,
            localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
            "/Pilot", "id:pilot-folder"), "Persisted containment") with
        {
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(
                SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery", "root-namespace")
        };
        var invalid = valid with
        {
            RecoveryRoot = valid.RecoveryRoot! with { RootPath = "/Elsewhere" }
        };
        var invalidJson = JsonSerializer.Serialize(invalid);
        string? aggregateHash = null;
        string? legacyHash = null;

        var aggregateRoot = Path.Combine(root, "aggregate");
        using (var appLock = PilotInstanceLock.Acquire(Path.Combine(aggregateRoot, "instance.lock")))
        {
            var raw = new DualSlotJsonStore<SyncProductState>(appLock.StateAuthority,
                SyncSharkRuntimePaths.ProductStateName);
            await raw.SaveAsync(new SyncProductState(new SyncPairSet([invalid], invalid.Id),
                new SyncRuntimeState([]), InstallationId: "0123456789abcdef0123456789abcdef"));
            var bytes = Directory.EnumerateFiles(appLock.StateAuthority.StateRoot,
                    SyncSharkRuntimePaths.ProductStateName + "-*.json")
                .Select(File.ReadAllBytes).Single(content =>
                    Encoding.UTF8.GetString(content).Contains("/Elsewhere", StringComparison.Ordinal));
            aggregateHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Equal(64, aggregateHash.Length);
            Throws<InvalidDataException>(() => new SyncPairStore(appLock.StateAuthority).Load());
            await raw.SaveAsync(new SyncProductState(new SyncPairSet([valid], valid.Id),
                new SyncRuntimeState([]), InstallationId: "0123456789abcdef0123456789abcdef"));
            Equal(valid.Id, new SyncPairStore(appLock.StateAuthority).Load().Pairs.Single().Id);
        }

        var legacyRoot = Path.Combine(root, "legacy");
        using (var appLock = PilotInstanceLock.Acquire(Path.Combine(legacyRoot, "instance.lock")))
        {
            var raw = new DualSlotJsonStore<SyncPairSet>(appLock.StateAuthority,
                SyncSharkRuntimePaths.PairSetStateName);
            await raw.SaveAsync(new SyncPairSet([invalid], invalid.Id));
            var bytes = Directory.EnumerateFiles(appLock.StateAuthority.StateRoot,
                    SyncSharkRuntimePaths.PairSetStateName + "-*.json")
                .Select(File.ReadAllBytes).Single(content =>
                    Encoding.UTF8.GetString(content).Contains("/Elsewhere", StringComparison.Ordinal));
            legacyHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Equal(64, legacyHash.Length);
            Throws<InvalidDataException>(() => new SyncPairStore(appLock.StateAuthority).Load());
            await raw.SaveAsync(new SyncPairSet([valid], valid.Id));
            Equal(valid.Id, new SyncPairStore(appLock.StateAuthority).Load().Pairs.Single().Id);
        }

        var authority = new SyncAuthorityDatabase(Path.Combine(root, "sqlite", "authority.db"));
        await authority.InitializeAsync("installation-containment", "profile-containment", "fixture", "{}");
        await authority.RegisterPairAsync(valid.Id, valid.PolicyRevision);
        await authority.RetainPairDefinitionAsync(valid, select: true);
        var builder = new SqliteConnectionStringBuilder { DataSource = authority.DatabasePath };
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;";
            command.Parameters.AddWithValue("$json", invalidJson);
            command.Parameters.AddWithValue("$id", valid.Id);
            Equal(1, await command.ExecuteNonQueryAsync());
        }
        await ThrowsAsync<InvalidDataException>(() => authority.LoadPairSetAsync());
        await ThrowsAsync<InvalidDataException>(() => authority.LoadAllPairDefinitionsAsync());
        string sqliteJson;
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pair_json FROM sync_pair WHERE pair_id=$id;";
            command.Parameters.AddWithValue("$id", valid.Id);
            sqliteJson = (string)(await command.ExecuteScalarAsync() ?? string.Empty);
        }
        Equal(invalidJson, sqliteJson);
        var sqliteHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sqliteJson)))
            .ToLowerInvariant();
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(valid));
            command.Parameters.AddWithValue("$id", valid.Id);
            Equal(1, await command.ExecuteNonQueryAsync());
        }
        Equal(valid.Id, (await authority.LoadPairSetAsync()).Pairs.Single().Id);
        Equal(valid.Id, (await authority.LoadAllPairDefinitionsAsync()).Single().Id);
        SqliteConnection.ClearAllPools();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            caseId = "TC3-CONTAIN-002-PERSISTED-INVALID-RESTART",
            aggregateSlotSha256 = aggregateHash,
            legacySlotSha256 = legacyHash,
            sqlitePairJsonSha256 = sqliteHash,
            acceptedControlPair = valid.Id
        }));
    });
}

static Task RuntimeRecoveryConsumerAdmissionContract(SyncPair invalidPair)
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var network = new NoNetworkHttpHandler();
        using var http = new HttpClient(network);
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http,
            new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
            new DropboxConnectionStore(appLock.StateAuthority), new MemoryDropboxRefreshTokenStore(),
            new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db")));
        var artifact = new SyncRecoveryArtifactProjection("artifact", "operation", invalidPair.Id, "object",
            "dropbox", "file.txt",
            "{\"recipe\":\"recreate_empty_directory\",\"identity\":\"id:recovery\",\"FileId\":\"id:recovery\",\"Revision\":\"rev:recovery\",\"RecoveryPath\":\"/Elsewhere/file.recovery\"}",
            new string('0', 64), 0, "verified", DateTimeOffset.UtcNow);
        var item = new SyncRecoveryCatalogItem("artifact", invalidPair.Id, invalidPair.Name,
            SyncRecoverySource.DropboxArchive, "file.txt", "Dropbox", "/Elsewhere/file.recovery", 0,
            DateTimeOffset.UtcNow, "retained", "available", new string('0', 64), "id:recovery",
            "rev:recovery", artifact.IdentityJson, true);
        await ThrowsAsync<InvalidDataException>(() =>
            host.VerifyDropboxRecoveryCopyAsync(invalidPair, artifact, CancellationToken.None));
        await ThrowsAsync<InvalidDataException>(() =>
            host.RemoveDropboxRecoveryCopyAsync(invalidPair, artifact, false, CancellationToken.None));
        await ThrowsAsync<InvalidDataException>(() =>
            host.RestoreDropboxArchiveAsync(invalidPair, item, "file.txt", "command-0123456789abcdef0123456789abcdef",
                CancellationToken.None));
        await ThrowsAsync<InvalidDataException>(() =>
            host.RestoreDropboxDirectoryRecipeAsync(invalidPair, artifact, "folder",
                "command-0123456789abcdef0123456789abcdef", CancellationToken.None));
        await ThrowsAsync<InvalidDataException>(() =>
            host.RestoreDropboxRevisionAsync(invalidPair, item with { Source = SyncRecoverySource.DropboxRevision },
                "file.txt", false, "command-0123456789abcdef0123456789abcdef", CancellationToken.None));
        Equal(0, network.CallCount);
    });
}

static Task ProductLikePairBindingContract()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairs = new SyncPairStore(appLock.StateAuthority);
        var connections = new DropboxConnectionStore(appLock.StateAuthority);
        await connections.SaveNewAsync(Binding().Normalize());
        using var http = new HttpClient(new PairBindingIdentityHandler());
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairs,
            new SyncRuntimeStateStore(appLock.StateAuthority), connections,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority);
        var result = await host.BindPairAsync(new SyncPairBindingRequest(null, "Product-like user root",
            localRoot,
            new DropboxFolderEntry("Sync Shark customer files", "/Sync Shark customer files",
                "id:product-like"),
            new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                "id:recovery-root"), true, 0, true));
        Equal("/Sync Shark customer files", result.Pair.DropboxRoot);
        Equal(SyncDropboxArtifactPaths.RecoveryContainerPath, result.Pair.RecoveryRoot!.RootPath);
        Equal(result.Pair.Id, result.PairSet.Pairs.Single().Id);
    });
}

static Task ManyPairAuthorityContract()
{
    return WithTempRoot(async root =>
    {
        const int pairCount = 24;
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-many-pairs", "profile-many-pairs",
            "many-pairs-test", "{}");
        var expectedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < pairCount; index++)
        {
            var local = Path.Combine(root, "pair-" + index.ToString("D2"));
            Directory.CreateDirectory(local);
            using var lease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                "/Many Pairs/" + index.ToString("D2"), "id:many-pair-" + index.ToString("D2")),
                "Pair " + index.ToString("D2"));
            expectedIds.Add(pair.Id);
            await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
            await authority.RetainPairDefinitionAsync(pair, select: index == 0);
        }

        var reloaded = await new SyncAuthorityDatabase(authority.DatabasePath).LoadPairSetAsync();
        Equal(pairCount, reloaded.Pairs.Count);
        True(reloaded.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(expectedIds));
    });
}

static Task MigratedPairFirstInventory()
{
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "nested/stable.txt", "same bytes");
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var legacyPair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
            localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
            "/Migrated Pair", "id:migrated-pair"), "Migrated pair") with
        {
            ControlNamespace = null,
            RecoveryRoot = null,
            InitialReviewApproved = null
        };
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        await pairStore.UpsertAsync(legacyPair);
        var connection = Binding().Normalize();
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(connection);
        var credentialStore = new MemoryDropboxRefreshTokenStore("retained-refresh-token");
        var remote = new FakeSyncRemoteStore();
        remote.Put("nested/stable.txt", "same bytes");
        var authorityPath = Path.Combine(root, "vNext", "state", "syncshark.db");

        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, new HttpClient(), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   new SyncAuthorityDatabase(authorityPath), (_, _) => remote))
        {
            var imported = (await host.LoadPairsAsync()).Pairs.Single();
            True(imported.ControlNamespace is null && imported.InitialReviewApproved == false);
            var first = await host.RunPairsAsync([imported], "Manual migration inventory");
            True(first.Items.Single().FailureMessage is null);
            var activated = (await host.LoadPairsAsync()).Pairs.Single();
            True(activated.ControlNamespace is not null && activated.InitialReviewApproved == false);
        }

        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority, new HttpClient(), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   new SyncAuthorityDatabase(authorityPath), (_, _) => remote))
        {
            var pair = (await restarted.LoadPairsAsync()).Pairs.Single();
            True(pair.ControlNamespace is not null);
            var second = await restarted.RunPairsAsync([pair], "Restarted migration inventory");
            True(second.Items.Single().FailureMessage is null);
        }

        Equal(0, remote.UploadCount);
        Equal(0, remote.DownloadCount);
        Equal(0, remote.DeleteCount);
        Equal(0, remote.MoveCount);
        Equal("same bytes", await File.ReadAllTextAsync(Path.Combine(localRoot, "nested", "stable.txt")));
    });
}

static Task LocalMonitorAndUsnContract()
{
    return WithTempRoot(async root =>
    {
        var checkpoint = new NtfsUsnCheckpoint(0x1234, 100, 500, 200);
        True(!checkpoint.RequiresFullReconciliation("0000000000001234:350"));
        True(!checkpoint.RequiresFullReconciliation(checkpoint.Cursor));
        True(checkpoint.RequiresFullReconciliation("0000000000001234:199"));
        True(checkpoint.RequiresFullReconciliation("0000000000001234:501"));
        True(checkpoint.RequiresFullReconciliation("0000000000004321:350"));
        True(checkpoint.RequiresFullReconciliation("malformed"));

        var localRoot = Path.Combine(root, "monitored");
        Directory.CreateDirectory(localRoot);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-monitor-contract", "Monitor contract", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Monitor Contract", "id:monitor-contract");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-monitor-contract", "profile-monitor-contract",
            "monitor-contract-test", "{}");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await authority.RegisterRootBindingAsync("root-monitor-contract-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);

        async Task<TimeSpan> ObserveAsync(string fileName)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var monitor = new SyncAuthorityLocalMonitor(pair, authority,
                "root-monitor-contract-local", _ =>
                {
                    signal.TrySetResult();
                    return Task.CompletedTask;
                });
            await monitor.StartAsync();
            var timer = Stopwatch.StartNew();
            await File.WriteAllTextAsync(Path.Combine(localRoot, fileName), fileName);
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            timer.Stop();
            return timer.Elapsed;
        }

        var firstLatency = await ObserveAsync("first.txt");
        var beforeRestart = (await authority.LoadMonitorProjectionAsync())
            .Single(item => item.PairId == pair.Id && item.Kind == "directory_changes").PendingDirtyItems;
        var restartLatency = await ObserveAsync("after-restart.txt");
        var afterRestart = (await new SyncAuthorityDatabase(authority.DatabasePath)
                .LoadMonitorProjectionAsync())
            .Single(item => item.PairId == pair.Id && item.Kind == "directory_changes").PendingDirtyItems;
        True(firstLatency < TimeSpan.FromSeconds(3));
        True(restartLatency < TimeSpan.FromSeconds(3));
        True(beforeRestart >= 1 && afterRestart > beforeRestart);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "local_detection_latency",
            targetMilliseconds = 3000,
            firstMilliseconds = Math.Round(firstLatency.TotalMilliseconds),
            restartMilliseconds = Math.Round(restartLatency.TotalMilliseconds)
        }));

        SyncAuthorityLocalMonitor.EventQueueCapacityForTest = 2;
        try
        {
            async Task<int> FloodAsync(string prefix)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var monitor = new SyncAuthorityLocalMonitor(pair, authority,
                    "root-monitor-contract-local", _ =>
                    {
                        signal.TrySetResult();
                        return Task.CompletedTask;
                    });
                await monitor.StartAsync();
                for (var index = 0; index < 10_000; index++)
                    monitor.EnqueueForTest(prefix + "-" + index + ".txt");
                await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return (await authority.LoadMonitorProjectionAsync())
                    .Single(item => item.PairId == pair.Id && item.Kind == "directory_changes").PendingDirtyItems;
            }

            var afterFlood = await FloodAsync("burst");
            True(afterFlood > afterRestart);
            True(afterFlood - afterRestart <= 4);
            True(await authority.HasPendingPairDirtyHintForTestAsync(pair.Id));
            var retainedCursor = (await authority.LoadMonitorCursorAsync(
                "monitor-local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    pair.Id + "\nroot-monitor-contract-local"))).ToLowerInvariant()[..32]))!.CursorValue;
            True(!string.IsNullOrWhiteSpace(retainedCursor));

            var afterFloodRestart = await FloodAsync("restart-burst");
            True(afterFloodRestart > afterFlood);
            True(await authority.HasPendingPairDirtyHintForTestAsync(pair.Id));

            // Reproduce the rejected predecessor's decisive boundary: 1,000 distinct
            // path hints plus the overflow sentinel must enter one valid database batch.
            // Repeated database failure must retain the same bounded work and eventually
            // commit instead of preserving an uncommittable 1,001-input retry forever.
            const int delayedFailures = 4;
            var delayedAttempts = 0;
            var overflowPersistedBeforeDiscard = false;
            var retainedBatchCounts = new List<int>();
            var delayedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SyncAuthorityLocalMonitor? delayedMonitor = null;
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = 1_001;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = TimeSpan.Zero;
            SyncAuthorityLocalMonitor.BeforeCommitForTest = async (pendingCount, _) =>
            {
                retainedBatchCounts.Add(pendingCount);
                var attempt = Interlocked.Increment(ref delayedAttempts);
                if (attempt == 1)
                {
                    var cursor = await authority.LoadMonitorCursorAsync(delayedMonitor!.CursorId);
                    overflowPersistedBeforeDiscard = cursor is { Status: "unavailable" };
                }
                if (attempt > delayedFailures) return;
                for (var index = 0; index < 2_000; index++)
                    delayedMonitor!.EnqueueForTest($"database-delay-retry-{attempt:D2}-{index:D4}.txt");
                throw new IOException("simulated delayed monitor database consumer");
            };
            await using (delayedMonitor = new SyncAuthorityLocalMonitor(pair, authority,
                             "root-monitor-contract-local", _ =>
                             {
                                 delayedCommit.TrySetResult();
                                 return Task.CompletedTask;
                             }))
            {
                await delayedMonitor.StartAsync();
                for (var index = 0; index < 10_000; index++)
                    delayedMonitor.EnqueueForTest($"database-delay-{index:D5}.txt");
                await delayedCommit.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            True(delayedAttempts > delayedFailures);
            True(overflowPersistedBeforeDiscard);
            True(retainedBatchCounts.Count > delayedFailures);
            True(retainedBatchCounts.Max() <= 1_000);
            True(await authority.HasPendingPairDirtyHintForTestAsync(pair.Id));

            // Crash/restart boundary: persist an unavailable cursor while every commit fails,
            // dispose the producer with uncommitted hints, then start a new monitor without
            // injecting another filesystem event. The retained uncertainty itself must admit
            // one pair-dirty record and return the cursor to active only after durable commit.
            var failurePersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedRestartAttempts = 0;
            SyncAuthorityLocalMonitor.BeforeCommitForTest = (_, _) =>
            {
                Interlocked.Increment(ref failedRestartAttempts);
                return Task.FromException(new IOException("simulated process-ending monitor commit failure"));
            };
            await using (var failedMonitor = new SyncAuthorityLocalMonitor(pair, authority,
                             "root-monitor-contract-local", _ => Task.CompletedTask,
                             _ =>
                             {
                                 failurePersisted.TrySetResult();
                                 return Task.CompletedTask;
                             }))
            {
                await failedMonitor.StartAsync();
                for (var index = 0; index < 10_000; index++)
                    failedMonitor.EnqueueForTest($"restart-loss-{index:D5}.txt");
                await failurePersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            True(failedRestartAttempts >= 1);
            var unavailableCursor = await authority.LoadMonitorCursorAsync(
                "monitor-local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    pair.Id + "\nroot-monitor-contract-local"))).ToLowerInvariant()[..32]);
            True(unavailableCursor is { Status: "unavailable" });

            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            var restartedDirty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var restartedMonitor = new SyncAuthorityLocalMonitor(pair, authority,
                             "root-monitor-contract-local", _ =>
                             {
                                 restartedDirty.TrySetResult();
                                 return Task.CompletedTask;
                             }))
            {
                await restartedMonitor.StartAsync();
                await restartedDirty.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var activeCursor = await authority.LoadMonitorCursorAsync(unavailableCursor!.MonitorCursorId);
            True(activeCursor is { Status: "active" });
            True(await authority.HasPendingPairDirtyHintForTestAsync(pair.Id));
        }
        finally
        {
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = null;
        }

        const string dropboxRootBindingId = "root-monitor-contract-dropbox";
        await authority.RegisterRootBindingAsync(dropboxRootBindingId, pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = pair.DropboxFolderId }), pair.DropboxRoot);

        // The recovered pair-dirty hint is consumed only by complete observations. Establish
        // matching two-sided baseline state, then prove a post-restart matching addition
        // converges through the production observer/planner path without a content effect.
        var convergenceRemote = new FakeSyncRemoteStore();
        convergenceRemote.PutWithId("first.txt", "first.txt", "id:monitor:first");
        convergenceRemote.PutWithId("after-restart.txt", "after-restart.txt", "id:monitor:after-restart");
        var observer = new SyncAuthorityObservationCoordinator(pair, convergenceRemote, authority,
            "root-monitor-contract-local", dropboxRootBindingId);
        await observer.ObserveAsync("monitor");
        await observer.ObserveAsync("monitor");
        _ = await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "converged.txt"), "converged");
        convergenceRemote.PutWithId("converged.txt", "converged", "id:monitor:converged");
        await observer.ObserveAsync("monitor");
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var convergence = await new SyncAuthorityReconciliationPlanner(authority,
                new SyncAuthorityPlannerOptions(1, "profile-monitor-contract", "control-local-monitor",
                    "control-dropbox-monitor"), admission: admission)
            .PlanAsync(pair.Id);
        var converged = convergence.Single(item => item.RelativePath == "converged.txt");
        Equal(SyncAuthorityPlannerOutcomeKind.Adopted, converged.Kind);
        Equal(0, convergenceRemote.UploadCount);
        Equal(0, convergenceRemote.DownloadCount);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "monitor_overflow_restart_convergence",
            maximumDatabaseBatch = 1_000,
            overflowPersistedBeforeDiscard = true,
            unavailablePersistedAcrossDispose = true,
            restartCommittedPairDirtyWithoutNewWatcherEvent = true,
            authoritativeRescanConverged = true
        }));

        var remote = new FakeSyncRemoteStore { CycleNextChangeCursorSequence = true };
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var monitor = new SyncAuthorityDropboxMonitor(pair, remote, remote, authority,
                         dropboxRootBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(10), _ =>
                         {
                             unavailable.TrySetResult();
                             return Task.CompletedTask;
                         }))
        {
            await monitor.StartAsync();
            await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retained = await authority.LoadMonitorCursorAsync(monitor.CursorId);
            True(retained is not null && retained.Status == "unavailable");
            Equal("cursor-b", retained!.CursorValue!);
        }
    });
}

static Task RecoveryBrowserLocalRestoreContract()
{
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "documents/recover-me.txt", "protected original");
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var connection = Binding().Normalize();
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                "/Recovery Contract", "id:recovery-contract"), "Recovery contract",
            connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = true,
            RecoveryRetentionDays = null,
            RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                "id:recovery-root", connection.RootNamespaceId)
        };
        pair = await BindFixtureControlAsync(pair, localLease,
            await pairStore.GetOrCreateInstallationIdAsync());
        await pairStore.UpsertAsync(pair);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(connection);
        var credential = new MemoryDropboxRefreshTokenStore("retained-refresh-token");
        var remote = new FakeSyncRemoteStore();
        remote.Put("documents/recover-me.txt", "protected original");
        var network = new NoNetworkHttpHandler();
        using var http = new HttpClient(network);
        var authorityPath = Path.Combine(root, "vNext", "state", "syncshark.db");
        var authority = new SyncAuthorityDatabase(authorityPath);
        SyncRecoveryCatalogItem retainedOriginal;

        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credential,
                   authority, (_, _) => remote))
        {
            pair = (await host.LoadPairsAsync()).Pairs.Single();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var baseline = await host.RunPairsAsync([pair], "Recovery baseline");
                True(baseline.Items.Single().FailureMessage is null);
                pair = (await host.LoadPairsAsync()).Pairs.Single();
            }
            remote.Delete("documents/recover-me.txt");
            string? batchFailure = null;
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception => batchFailure = exception.ToString();
            for (var attempt = 0; attempt < 3 &&
                 File.Exists(Path.Combine(localRoot, "documents", "recover-me.txt")); attempt++)
            {
                var deletion = await host.RunPairsAsync([pair], "Recovery deletion");
                var item = deletion.Items.Single();
                if (item.FailureMessage is not null)
                {
                    var queue = await host.LoadQueueProjectionAsync();
                    var details = new List<object>();
                    foreach (var queued in queue.Where(candidate => candidate.PairId == pair.Id))
                    {
                        var operation = await host.Authority.LoadOperationAsync(queued.OperationId);
                        details.Add(new
                        {
                            queued.OperationId,
                            queued.Operation,
                            queued.State,
                            members = operation.Members.Select(member => member.Role).ToArray(),
                            allocations = operation.Allocations.Select(allocation => allocation.Kind.ToString()).ToArray()
                        });
                    }
                    throw new InvalidOperationException(
                        $"Recovery deletion failed with {item.FailureType}: {item.FailureMessage}; retained={JsonSerializer.Serialize(details)}; stack={batchFailure}");
                }
            }
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
            True(!File.Exists(Path.Combine(localRoot, "documents", "recover-me.txt")));

            var catalog = await host.LoadRecoveryCatalogAsync(pair.Id);
            retainedOriginal = catalog.Single(item => item.Source == SyncRecoverySource.LocalArchive &&
                                                       item.RelativePath == "documents/recover-me.txt");
            Equal("Dropbox · " + connection.ConnectionId, retainedOriginal.Connection);
            Equal((long)"protected original".Length, retainedOriginal.Size);
            True(retainedOriginal.CanRestore && retainedOriginal.Health.Contains("Available", StringComparison.Ordinal));

            Write(localRoot, "documents/recover-me.txt", "new occupant");
            var preview = await host.PreviewRecoveryAsync(retainedOriginal, restoreAsCopy: false);
            True(preview.DestinationExists && preview.Summary.Contains("retained in Recovery", StringComparison.Ordinal));
            var restored = await host.RestoreRecoveryAsync(retainedOriginal, restoreAsCopy: false);
            Equal("documents/recover-me.txt", restored.TargetRelativePath);
            Equal("protected original", await File.ReadAllTextAsync(
                Path.Combine(localRoot, "documents", "recover-me.txt")));
            var afterRestore = await host.LoadRecoveryCatalogAsync(pair.Id);
            True(afterRestore.Count(item => item.Source == SyncRecoverySource.LocalArchive) >= 2);
            True(afterRestore.Any(item => item.Source == SyncRecoverySource.LocalArchive &&
                                          item.ContentHash != retainedOriginal.ContentHash));

            var sourceArtifact = (await authority.LoadRecoveryArtifactsAsync()).Single(item =>
                item.RecoveryArtifactId == retainedOriginal.Key);
            var recoveryContainer = Path.Combine(pair.ControlNamespace!.Local.RootPath,
                SyncLocalRecovery.RecoveryFolderPrefix + pair.Id);
            Directory.CreateDirectory(recoveryContainer);
            var directoryArchivePath = Path.Combine(recoveryContainer, "directory-cleanup.recovery");
            Directory.CreateDirectory(Path.Combine(directoryArchivePath, "nested"));
            Write(directoryArchivePath, "nested/retained.txt", "directory recovery bytes");
            PilotDirectoryIdentity directoryIdentity;
            using (var directoryLease = PilotDirectoryLease.Open(directoryArchivePath))
                directoryIdentity = directoryLease.Identity;
            var directoryManifest = await SyncAuthorityCrossPairMoveCoordinator
                .BuildDirectoryManifestAsync("dropbox", directoryArchivePath, CancellationToken.None);
            const string directoryArtifactId = "directorycleanup01";
            await authority.RegisterRecoveryArtifactAsync(directoryArtifactId, sourceArtifact.OperationId,
                sourceArtifact.ObjectId, null, "local", JsonSerializer.Serialize(new
                {
                    path = directoryArchivePath,
                    identity = $"{directoryIdentity.VolumeSerialNumber:x8}:{directoryIdentity.FileId:x16}",
                    kind = "directory"
                }), directoryManifest.Hash, directoryManifest.Size);
            var directoryCatalog = (await host.LoadRecoveryCatalogAsync(pair.Id)).Single(item =>
                item.Key == directoryArtifactId);
            var directoryCleanup = await host.RemoveRecoveryCopyAsync(directoryCatalog);
            True(directoryCleanup.Detail.Contains("removed", StringComparison.OrdinalIgnoreCase));
            True(!Directory.Exists(directoryArchivePath));

            var changedDirectoryPath = Path.Combine(recoveryContainer, "directory-mismatch.recovery");
            Directory.CreateDirectory(changedDirectoryPath);
            Write(changedDirectoryPath, "retained.txt", "original directory bytes");
            PilotDirectoryIdentity changedDirectoryIdentity;
            using (var changedDirectoryLease = PilotDirectoryLease.Open(changedDirectoryPath))
                changedDirectoryIdentity = changedDirectoryLease.Identity;
            var changedDirectoryManifest = await SyncAuthorityCrossPairMoveCoordinator
                .BuildDirectoryManifestAsync("dropbox", changedDirectoryPath, CancellationToken.None);
            const string changedDirectoryArtifactId = "directorymismatch01";
            await authority.RegisterRecoveryArtifactAsync(changedDirectoryArtifactId,
                sourceArtifact.OperationId, sourceArtifact.ObjectId, null, "local", JsonSerializer.Serialize(new
                {
                    path = changedDirectoryPath,
                    identity = $"{changedDirectoryIdentity.VolumeSerialNumber:x8}:{changedDirectoryIdentity.FileId:x16}",
                    kind = "directory"
                }), changedDirectoryManifest.Hash, changedDirectoryManifest.Size);
            var changedDirectoryCatalog = (await host.LoadRecoveryCatalogAsync(pair.Id)).Single(item =>
                item.Key == changedDirectoryArtifactId);
            Write(changedDirectoryPath, "unexpected.txt", "changed after retention");
            await ThrowsAsync<IOException>(() => host.RemoveRecoveryCopyAsync(changedDirectoryCatalog));
            True(Directory.Exists(changedDirectoryPath));
            True((await authority.LoadRecoveryArtifactsAsync()).Single(item =>
                item.RecoveryArtifactId == changedDirectoryArtifactId).Status == "retained");
            await host.RetirePairAsync(pair.Id);
        }

        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credential,
                   new SyncAuthorityDatabase(authorityPath), (_, _) => remote))
        {
            True(!(await restarted.LoadPairsAsync()).Pairs.Any());
            var deletedChoice = (await restarted.LoadRecoveryPairChoicesAsync()).Single(item =>
                item.Id == pair.Id);
            True(!deletedChoice.Active && deletedChoice.DisplayName.Contains("deleted pair", StringComparison.Ordinal));
            var afterRestart = await restarted.LoadRecoveryCatalogAsync(pair.Id);
            var original = afterRestart.Single(item => item.Key == retainedOriginal.Key);
            var preview = await restarted.PreviewRecoveryAsync(original, restoreAsCopy: true);
            True(!preview.DestinationExists && preview.RestoreAsCopy &&
                 preview.TargetRelativePath != original.RelativePath);
            var copy = await restarted.RestoreRecoveryAsync(original, restoreAsCopy: true);
            Equal("protected original", await File.ReadAllTextAsync(
                PilotPath.ResolveContainedPath(localRoot, copy.TargetRelativePath)));
            await restarted.Authority.AuthorizeRecoveryArtifactReleaseAsync(original.Key);
            await using (var crashWindow = SyncLocalFileAuthority.OpenExistingExclusive(original.Location))
                crashWindow.DeleteOnClose();
            True(!File.Exists(original.Location));
            var cleanup = await restarted.RemoveRecoveryCopyAsync(original);
            True(cleanup.Detail.Contains("removed", StringComparison.OrdinalIgnoreCase));
            True(!(await restarted.LoadRecoveryCatalogAsync(pair.Id)).Any(item =>
                item.Key == retainedOriginal.Key));
        }

        Equal(0, network.CallCount);
    });
}

static Task RecoveryBrowserDropboxRestoreContract()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    return WithTempRoot(async root =>
    {
        const string relativePath = "recover-me.txt";
        var protectedBytes = Encoding.UTF8.GetBytes("protected Dropbox original");
        var currentBytes = Encoding.UTF8.GetBytes("current Dropbox occupant");
        var historicalBytes = Encoding.UTF8.GetBytes("historical Dropbox revision");
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllBytesAsync(Path.Combine(localRoot, relativePath), protectedBytes);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var credential = new MemoryDropboxRefreshTokenStore("retained-refresh-token");
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var remote = new FakeSyncRemoteStore();
        remote.Put(relativePath, Encoding.UTF8.GetString(protectedBytes));
        var handler = new DropboxRecoveryBrowserContractHandler();
        using var http = new HttpClient(handler);
        var authorityPath = Path.Combine(root, "vNext", "state", "syncshark.db");
        var authority = new SyncAuthorityDatabase(authorityPath);
        SyncPair pair;
        SyncRecoveryArtifactProjection archive;

        using (var fixtureHost = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credential,
                   authority, (_, _) => remote))
        {
            var bound = await fixtureHost.BindPairAsync(new SyncPairBindingRequest(null,
                "Dropbox recovery contract", localRoot,
                new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
                new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery-root"),
                Enabled: true, Priority: 0, PropagateDeletions: true));
            pair = bound.Pair;
            var discovery = await fixtureHost.RunPairsAsync([pair], "Dropbox recovery discovery");
            True(discovery.Items.Single().FailureMessage is null);
            pair = await fixtureHost.ApproveBootstrapMergeAsync(pair.Id);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var baseline = await fixtureHost.RunPairsAsync([pair], "Dropbox recovery baseline");
                True(baseline.Items.Single().FailureMessage is null);
                pair = (await fixtureHost.LoadPairsAsync()).Pairs.Single();
            }

            File.Delete(Path.Combine(localRoot, relativePath));
            for (var attempt = 0; attempt < 3 && remote.Exists(relativePath); attempt++)
            {
                var deletion = await fixtureHost.RunPairsAsync([pair], "Dropbox recovery deletion");
                True(deletion.Items.Single().FailureMessage is null);
                pair = (await fixtureHost.LoadPairsAsync()).Pairs.Single();
            }
            True(!remote.Exists(relativePath));
            archive = (await authority.LoadRecoveryArtifactsAsync()).Single(item =>
                item.Side == "dropbox" && item.OriginalRelativePath == relativePath);
        }

        handler.Configure(archive, protectedBytes, currentBytes, historicalBytes);
        SyncRecoveryCatalogItem retainedArchive;
        string restoredCopyPath;
        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credential,
                   authority))
        {
            pair = (await host.LoadPairsAsync()).Pairs.Single();
            var firstPage = await host.LoadRecoveryCatalogPageAsync(pair.Id, pageSize: 1,
                includeDropboxDeletedItems: true);
            retainedArchive = firstPage.Items.Single(item => item.Source == SyncRecoverySource.DropboxArchive);
            True(retainedArchive.CanRestore &&
                 retainedArchive.Health.Contains("stable cloud identity", StringComparison.Ordinal),
                retainedArchive.Health);
            True(firstPage.Items.Any(item => item.Source == SyncRecoverySource.DropboxDeleted &&
                                             item.RelativePath == "deleted-first.txt"));
            True(firstPage.HasMoreDeleted && firstPage.DeletedCursor == "deleted-cursor-1");
            var secondPage = await host.LoadRecoveryCatalogPageAsync(pair.Id,
                archiveOffset: firstPage.NextArchiveOffset, deletedCursor: firstPage.DeletedCursor,
                includeDropboxDeletedItems: true, pageSize: 1);
            True(secondPage.Items.Any(item => item.Source == SyncRecoverySource.DropboxDeleted &&
                                              item.RelativePath == "deleted-second.txt"));
            True(!secondPage.HasMoreDeleted && secondPage.DeletedCursor is null);

            var revisions = await host.LoadDropboxRevisionCatalogAsync(pair.Id, relativePath);
            var historical = revisions.Single(item => item.Revision ==
                                                       DropboxRecoveryBrowserContractHandler.HistoricalRevision);
            Equal(DropboxContentHash.Compute(historicalBytes), historical.ContentHash);
            var copyPreview = await host.PreviewRecoveryAsync(historical, restoreAsCopy: true);
            True(copyPreview.RestoreAsCopy && !copyPreview.DestinationExists &&
                 copyPreview.TargetRelativePath != relativePath);
            var restoredCopy = await host.RestoreRecoveryAsync(historical, restoreAsCopy: true);
            restoredCopyPath = restoredCopy.TargetRelativePath;
            True(restoredCopyPath != relativePath);
            Equal(Encoding.UTF8.GetString(historicalBytes), handler.ReadPairText(restoredCopyPath));
            Equal(Encoding.UTF8.GetString(currentBytes), handler.ReadPairText(relativePath));

            var originalPreview = await host.PreviewRecoveryAsync(retainedArchive, restoreAsCopy: false);
            True(originalPreview.DestinationExists &&
                 originalPreview.Summary.Contains("retained in Recovery", StringComparison.Ordinal));
            var restoredOriginal = await host.RestoreRecoveryAsync(retainedArchive, restoreAsCopy: false);
            Equal(relativePath, restoredOriginal.TargetRelativePath);
            Equal(Encoding.UTF8.GetString(protectedBytes), handler.ReadPairText(relativePath));
            Equal(Encoding.UTF8.GetString(currentBytes), handler.RetainedPreimageText);
            True(handler.RevisionDownloadCount >= 2);
            Equal(1, handler.ArchiveRestoreCopyCount);
            Equal(1, handler.ConditionalDeleteCount);
        }

        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credential,
                   new SyncAuthorityDatabase(authorityPath)))
        {
            pair = (await restarted.LoadPairsAsync()).Pairs.Single();
            var retained = await restarted.LoadRecoveryCatalogAsync(pair.Id);
            True(retained.Any(item => item.Key == retainedArchive.Key));
            True(retained.Count(item => item.Source == SyncRecoverySource.DropboxArchive) >= 2);
            Equal(Encoding.UTF8.GetString(historicalBytes), handler.ReadPairText(restoredCopyPath));
        }
        Equal(1, handler.DeletedContinuationCount);
    });
}

static Task RecursiveNamespaceFailureContract()
{
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        var outsideRoot = Path.Combine(root, "outside");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(outsideRoot);
        await File.WriteAllTextAsync(Path.Combine(outsideRoot, "must-not-enter.txt"), "outside");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "locked.txt"), "locked bytes");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "CaseOnly.txt"), "case bytes");
        File.Move(Path.Combine(localRoot, "CaseOnly.txt"), Path.Combine(localRoot, "case-stage.tmp"));
        File.Move(Path.Combine(localRoot, "case-stage.tmp"), Path.Combine(localRoot, "caseonly.txt"));
        var junctionPath = Path.Combine(localRoot, "loop-link");
        CreateDirectoryJunction(junctionPath, outsideRoot);
        try
        {
            await using var locked = new FileStream(Path.Combine(localRoot, "locked.txt"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            PilotLocalScan scan;
            using (var lease = PilotDirectoryLease.Open(localRoot))
                scan = await new PilotLocalScanner().ScanAsync(lease);
            True(scan.Decisions.Any(item => item.RelativePath == "locked.txt" &&
                                            item.Kind == PilotDecisionKind.Blocked &&
                                            item.Reason.Contains("locked", StringComparison.OrdinalIgnoreCase)));
            True(scan.Decisions.Any(item => item.RelativePath == "loop-link" &&
                                            item.Kind == PilotDecisionKind.Blocked &&
                                            item.Reason.Contains("reparse", StringComparison.OrdinalIgnoreCase)));
            True(!scan.Entries.Keys.Any(path => path.Contains("must-not-enter", StringComparison.OrdinalIgnoreCase)));
            True(scan.Entries.ContainsKey("caseonly.txt"));
            foreach (var unsupported in new[] { "CON.txt", "aux", "folder./file.txt", "trailing-space /x" })
                Throws<ArgumentException>(() => PilotPath.NormalizeRelative(unsupported));
        }
        finally
        {
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath, recursive: false);
        }
    });
}

static async Task DropboxInterruptedListingContract()
{
    async Task RunFailureAsync(DropboxListingFailureMode mode, HttpStatusCode? expectedStatus = null)
    {
        using var http = new HttpClient(new DropboxListingFailureHandler(mode));
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        try
        {
            _ = await client.ListEntriesAsync();
            throw new InvalidOperationException("An incomplete Dropbox inventory was accepted as complete.");
        }
        catch (DropboxApiException exception) when (expectedStatus is not null)
        {
            Equal(expectedStatus.Value, exception.StatusCode);
        }
        catch (InvalidDataException) when (mode is DropboxListingFailureMode.RepeatedCursor or
                                                   DropboxListingFailureMode.CursorCycle or
                                                   DropboxListingFailureMode.OversizedCursor)
        {
        }
        catch (ArgumentException) when (mode == DropboxListingFailureMode.UnsupportedName)
        {
        }
    }

    await RunFailureAsync(DropboxListingFailureMode.PermissionDenied, HttpStatusCode.Forbidden);
    await RunFailureAsync(DropboxListingFailureMode.InterruptedContinuation,
        HttpStatusCode.ServiceUnavailable);
    await RunFailureAsync(DropboxListingFailureMode.RepeatedCursor);
    await RunFailureAsync(DropboxListingFailureMode.CursorCycle);
    await RunFailureAsync(DropboxListingFailureMode.OversizedCursor);
    await RunFailureAsync(DropboxListingFailureMode.UnsupportedName);
}

static async Task DropboxRecursiveRootEchoContract()
{
    var handler = new DropboxContractHandler(rootEcho: true);
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder");
    var entries = await client.ListEntriesAsync();
    Equal(0, entries.Count);
    Equal("root", handler.PathRootTag);
    Equal("root-namespace", handler.PathRootNamespace);

    var accountRootHandler = new DropboxContractHandler(accountRoot: true);
    using var accountRootHttp = new HttpClient(accountRootHandler);
    var accountRootClient = new DropboxPilotClient(accountRootHttp, new FixedTokenSource(), "/", Binding(),
        PilotPath.DropboxNamespaceRootIdentity("root-namespace"));
    await accountRootClient.VerifyRootIdentityAsync();
    var accountEntries = await accountRootClient.ListEntriesAsync();
    Equal(string.Empty, accountRootHandler.ListPath);
    Equal("Documents", accountEntries.Single().RelativePath);
}

static async Task DropboxMountedInventoryContract()
{
    var handler = new DropboxContractHandler(mountedDescendants: true);
    using var http = new HttpClient(handler);
    var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    var entries = await client.ListEntriesAsync();
    Equal(true, handler.ListIncludesMounted);
    Equal(true, entries.Any(entry => entry.RelativePath == "Mounted/shared.txt"));
    var deleted = await client.ListDeletedEntriesPageAsync();
    Equal(true, handler.ListIncludesMounted);
    Equal("Mounted/deleted.txt", deleted.Entries.Single().RelativePath);
    await client.GetLatestCursorAsync();
    Equal(true, handler.CursorIncludesMounted);
    Equal("root-namespace", handler.PathRootNamespace);
    Console.WriteLine("PASS mounted Dropbox descendants are included in complete inventories, deletion listings and change cursors");
}

static Task ProjectionSchemaContract()
{
    return WithTempRoot(async root =>
    {
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-projection-contract", "profile-projection-contract",
            "projection-contract-test", "{}");
        await authority.RegisterPairAsync("pair-projection-contract", 1);
        Equal(0, (await authority.LoadOpenConflictIdsPageAsync(25, search: "nested")).Count);
        Equal(0, (await authority.LoadPairPathStatesAsync("pair-projection-contract")).Count);
    });
}

static async Task DropboxUpdateWireContract()
{
    var handler = new DropboxContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    await client.VerifyRootIdentityAsync();
    await using var source = new MemoryStream(Bytes("updated wire bytes"));
    var hash = await DropboxContentHash.ComputeAsync(source);
    source.Position = 0;
    await client.UploadAsync("wire.txt", source, hash, "rev:expected");
    Equal("update", handler.UploadMode);
    Equal("rev:expected", handler.UploadUpdateRevision);
    Equal("id:pilot-folder/wire.txt", handler.UploadPath);
}

static async Task TransferThroughputAndRateLimitContract()
{
    var limiter = new SyncTransferRateLimiter(new SyncTransferSettings(2, 128, 128));
    var aggregate = Stopwatch.StartNew();
    await Task.WhenAll(
        limiter.ThrottleAsync(SyncTransferDirection.Upload, 32 * 1024).AsTask(),
        limiter.ThrottleAsync(SyncTransferDirection.Upload, 32 * 1024).AsTask());
    aggregate.Stop();
    True(aggregate.Elapsed >= TimeSpan.FromMilliseconds(400) &&
         aggregate.Elapsed < TimeSpan.FromSeconds(3));

    limiter.Update(new SyncTransferSettings(2, 1, 1));
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
        await ThrowsAsync<OperationCanceledException>(() => limiter
            .ThrottleAsync(SyncTransferDirection.Download, 1024 * 1024, cancellation.Token).AsTask());

    var handler = new DropboxRateLimitContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder");
    var retry = Stopwatch.StartNew();
    await client.VerifyRootIdentityAsync();
    retry.Stop();
    Equal(3, handler.CallCount);
    Equal(3L, client.ProviderCallCount);
    True(retry.Elapsed >= TimeSpan.FromMilliseconds(45) && retry.Elapsed < TimeSpan.FromSeconds(3));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        metric = "provider_call_accounting",
        rateLimitAttempts = handler.CallCount,
        countedProviderCalls = client.ProviderCallCount,
        minimumRetryMilliseconds = 45,
        retryMilliseconds = Math.Round(retry.Elapsed.TotalMilliseconds),
        configuredUploadKiBPerSecond = 128,
        concurrentThrottleMilliseconds = Math.Round(aggregate.Elapsed.TotalMilliseconds)
    }));
}

static async Task CoordinatedTransferSummaryContract(string root)
{
    // Only provider input is synthetic. Production observation, planning, effects and
    // coordinated result aggregation all execute, including a second planning round.
    foreach (var scenario in new[] { "different", "revision-only", "uncertain-upload", "unknown-content" })
    {
        var scenarioRoot = Path.Combine(root, scenario);
        if (Directory.Exists(scenarioRoot)) throw new IOException("Use a fresh transfer-summary fixture root.");
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var localRoot = Path.Combine(scenarioRoot, "local");
        Write(localRoot, "anchor.txt", "unchanged anchor");
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/TransferSummary/" + scenario, "id:transfer-summary-" + scenario), scenario,
            connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = false,
            RecoveryRoot = new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                "id:transfer-summary-recovery", connection.RootNamespaceId)
        };
        pair = await BindFixtureControlAsync(pair, lease, await pairStore.GetOrCreateInstallationIdAsync());
        await pairStore.UpsertAsync(pair, select: true);
        var remote = new FakeSyncRemoteStore();
        remote.Put("anchor.txt", "unchanged anchor");
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
        using var http = new HttpClient(new NoNetworkHttpHandler());
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("fixture-refresh-token"), authority, (_, _) => remote);
        var priorMode = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var pairs = (await host.LoadPairsAsync()).Pairs;
            for (var pass = 0; pass < 2; pass++)
            {
                var baseline = await host.RunPairsAsync(pairs, "Transfer summary baseline");
                True(baseline.Items.All(item => item.FailureMessage is null));
                if (pass == 1)
                {
                    True(baseline.Items.Single().Result is { Uploaded: 0, Downloaded: 0 });
                }
            }
            var uploadsBefore = remote.UploadCount;
            var downloadsBefore = remote.DownloadCount + remote.RangeDownloadCount;
            if (scenario == "unknown-content")
            {
                var before = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id))
                    .Single(p => p.LocalRelativePath == "anchor.txt");
                remote.Put("anchor.txt", "unchanged anchor");
                remote.SuppressContentHashesForTest = true;
                var unknown = await host.RunPairsAsync(pairs, "Unknown content must not be adopted");
                var after = (await authority.LoadLatestSynchronizationPointsAsync(pair.Id))
                    .Single(p => p.LocalRelativePath == "anchor.txt");
                Equal(before.SynchronizationPointId, after.SynchronizationPointId);
                Equal(uploadsBefore, remote.UploadCount);
                Equal(downloadsBefore, remote.DownloadCount + remote.RangeDownloadCount);
                True(unknown.Items.All(i => i.Result is null || i.Result.Uploaded + i.Result.Downloaded == 0));
                Equal("unchanged anchor", File.ReadAllText(Path.Combine(localRoot, "anchor.txt")));
                Console.WriteLine("PASS real coordinated transfer summary: unknown-content rejected without adoption or transfer");
                continue;
            }
            const string initial = "initial transfer bytes";
            const string changed = "changed remote content";
            Write(localRoot, "roundtrip.txt", initial);
            var mutated = false;
            if (scenario == "uncertain-upload") remote.InterruptNextUploadBeforeEffect = true;
            host.CoordinatedPlanningPhaseForTest = phase =>
            {
                if (phase == "after-dispatch-before-next-observation-set" && !mutated &&
                    remote.UploadCount == uploadsBefore + 1 && scenario != "uncertain-upload")
                {
                    Equal(initial, remote.ReadText("roundtrip.txt"));
                    remote.Put("roundtrip.txt", scenario == "different" ? changed : initial);
                    mutated = true;
                }
                return Task.CompletedTask;
            };
            var batch = await host.RunPairsAsync(pairs, "Transfer summary actual roundtrip");
            var item = batch.Items.Single();
            if (item.FailureMessage is not null) throw new InvalidOperationException(item.FailureMessage);
            var result = item.Result ?? throw new InvalidOperationException("No coordinated result.");
            if (scenario == "uncertain-upload")
            {
                Equal(0, remote.UploadCount - uploadsBefore);
                Equal(0, result.Uploaded);
                Equal(0, result.Downloaded);
                True(!remote.Exists("roundtrip.txt"));
                Equal(initial, File.ReadAllText(Path.Combine(localRoot, "roundtrip.txt")));
            }
            else
            {
                True(mutated);
                Equal(1, remote.UploadCount - uploadsBefore);
                Equal(1, result.Uploaded);
                var expectedDownloads = scenario == "different" ? 1 : 0;
                Equal(expectedDownloads, remote.DownloadCount + remote.RangeDownloadCount - downloadsBefore);
                Equal(expectedDownloads, result.Downloaded);
                Equal(scenario == "different" ? changed : initial,
                    File.ReadAllText(Path.Combine(localRoot, "roundtrip.txt")));
                Equal(scenario == "different" ? SyncDecisionKind.Downloaded : SyncDecisionKind.Uploaded,
                    result.Decisions.Single(d => d.RelativePath == "roundtrip.txt").Kind);
            }
            Console.WriteLine("PASS real coordinated transfer summary: " + scenario);
        }
        finally
        {
            host.CoordinatedPlanningPhaseForTest = null;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = priorMode;
        }
    }
}

static Task PairConcurrencyContract()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);
        var activeByWave = new int[2];
        var maximumByWave = new int[2];
        var concurrencyReached = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        for (var index = 0; index < 4; index++)
        {
            var localRoot = Path.Combine(root, "pair-" + index);
            Directory.CreateDirectory(localRoot);
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/Concurrency/" + index, "id:concurrency-" + index), "Concurrency " + index,
                connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true
            };
            pair = await BindFixtureControlAsync(pair, lease,
                await pairStore.GetOrCreateInstallationIdAsync());
            await pairStore.UpsertAsync(pair, select: index == 0);
            var remote = new FakeSyncRemoteStore();
            var callNumber = -1;
            var activeWave = -1;
            remote.BeforeListEntriesForTest = async token =>
            {
                var wave = Interlocked.Increment(ref callNumber);
                activeWave = wave < activeByWave.Length ? wave : -1;
                if (activeWave < 0) return;
                var current = Interlocked.Increment(ref activeByWave[activeWave]);
                while (current > Volatile.Read(ref maximumByWave[activeWave]))
                {
                    var observed = Volatile.Read(ref maximumByWave[activeWave]);
                    if (Interlocked.CompareExchange(ref maximumByWave[activeWave], current, observed) == observed)
                        break;
                }
                if (current >= 2) concurrencyReached[activeWave].TrySetResult();
                await concurrencyReached[activeWave].Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            };
            remote.AfterListEntriesForTest = () =>
            {
                var wave = activeWave;
                if (wave >= 0) Interlocked.Decrement(ref activeByWave[wave]);
                activeWave = -1;
            };
            remotes[pair.Id] = remote;
        }

        var authority = new SyncAuthorityDatabase(Path.Combine(root, "vNext", "state", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, new HttpClient(), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        var pairs = (await host.LoadPairsAsync()).Pairs;
        var result = await host.RunPairsAsync(pairs, "Concurrency contract");
        Equal(4, result.Items.Count);
        True(result.Items.All(item => item.FailureMessage is null));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "pair_concurrency",
            configuredMaximum = 2,
            observedMaximum = maximumByWave.Max(),
            discoveryMaximum = maximumByWave[0],
            coordinatedObservationMaximum = maximumByWave[1]
        }));
        Equal(2, maximumByWave[0]);
        Equal(2, maximumByWave[1]);
    });
}

static Task CrossPairGenerationReproductionContract(bool onlyInvalidation = false)
{
    return WithTempRoot(async root =>
    {
        if (!onlyInvalidation)
        {
            foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
            foreach (var scenario in new[]
                     {
                         (Name: "control-disabled", Coordinated: false, Phase: (string?)null),
                         (Name: "during-observation-gap", Coordinated: true, Phase: (string?)null),
                         (Name: "during-observation-duplicate", Coordinated: true, Phase: (string?)null),
                         (Name: "after-observation-set", Coordinated: true,
                             Phase: "after-observation-set-bound-before-cross-pair-correlation"),
                         (Name: "after-cross-pair-reservations", Coordinated: true,
                             Phase: "after-cross-pair-reservations-before-ordinary-planning"),
                          (Name: "cross-pair-reservation-refresh", Coordinated: true,
                              Phase: "after-observation-set-bound-before-cross-pair-correlation"),
                          (Name: "manual-single-pair", Coordinated: true, Phase: (string?)null),
                          (Name: "after-ordinary-planning", Coordinated: true,
                              Phase: "after-ordinary-planning-before-dispatch"),
                         (Name: "after-dispatch", Coordinated: true,
                             Phase: "after-dispatch-before-next-observation-set")
                     })
            {
            var coordinated = scenario.Coordinated;
            var comparison = scenario.Name;
            var scenarioRoot = Path.Combine(root, direction + "-" + comparison);
            Directory.CreateDirectory(scenarioRoot);
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var installationId = await pairStore.GetOrCreateInstallationIdAsync();
            var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

            async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content,
                string providerIdentity)
            {
                var localRoot = Path.Combine(scenarioRoot, name);
                Directory.CreateDirectory(localRoot);
                Write(localRoot, relativePath, content);
                using var lease = PilotDirectoryLease.Open(localRoot);
                var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                        lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                        "/Generation/" + name, "id:generation-root-" + direction + "-" + name),
                    name, connection.RootNamespaceId) with
                {
                    ConnectionId = connection.ConnectionId,
                    InitialReviewApproved = true,
                    PropagateDeletions = false,
                    Priority = scenario.Name == "during-observation-duplicate" && name == "source" ||
                               scenario.Name == "during-observation-gap" && name == "destination" ? 10 : 0
                };
                pair = await BindFixtureControlAsync(pair, lease, installationId);
                await pairStore.UpsertAsync(pair, select: name == "source");
                var remote = new FakeSyncRemoteStore();
                remote.PutWithId(relativePath, content, providerIdentity);
                remotes[pair.Id] = remote;
                return pair;
            }

            var source = await CreatePairAsync("source", "moved.txt", "move-payload",
                "id:generation-stable-object");
            var destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
                "id:generation-destination-anchor");
            var unrelated = await CreatePairAsync("unrelated", "independent.txt", "old-independent",
                "id:generation-unrelated");
            var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
                "syncshark.db"));
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new NoNetworkHttpHandler()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                (pair, _) => remotes[pair.Id]);
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = !coordinated;
            try
            {
                await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
                var pairs = (await host.LoadPairsAsync()).Pairs;
                var baseline = await host.RunPairsAsync(pairs, "Generation baseline");
                True(baseline.Items.All(item => item.FailureMessage is null));
                var stable = await host.RunPairsAsync(pairs, "Generation stable baseline");
                True(stable.Items.All(item => item.FailureMessage is null));

                PilotLocalEntry sourceLocalIdentityBefore;
                using (var sourceLease = PilotDirectoryLease.Open(source.LocalRoot,
                           new PilotDirectoryIdentity(source.LocalVolumeSerialNumber,
                               source.LocalRootFileId)))
                    sourceLocalIdentityBefore = (await new PilotLocalScanner().ScanAsync(sourceLease))
                        .Entries["moved.txt"];

                remotes[unrelated.Id].PutWithId("progress.txt", "independent-progress",
                    "id:generation-unrelated-progress");
                var injected = 0;
                var reservationInvalidated = 0;
                var observedWorkers = 0;
                var twoWorkersReached = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                async Task InjectMoveCoreAsync()
                {
                    if (Interlocked.CompareExchange(ref injected, 1, 0) != 0) return;
                    if (direction == "local-to-dropbox")
                    {
                        File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
                            Path.Combine(destination.LocalRoot, "moved.txt"));
                        return;
                    }
                    var expected = (await remotes[source.Id].ListEntriesAsync())
                        .Single(item => item.RelativePath == "moved.txt");
                    await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved.txt");
                }
                async Task InjectMoveFromProgressAsync(SyncAuthorityBatchProgress progress)
                {
                    if (string.Equals(progress.State, "Running", StringComparison.Ordinal))
                        await InjectMoveCoreAsync();
                }
                var sourceMoveBefore = remotes[source.Id].MoveCount;
                if (scenario.Name == "manual-single-pair")
                {
                    await InjectMoveCoreAsync();
                }
                else if (scenario.Name == "cross-pair-reservation-refresh")
                {
                    await InjectMoveCoreAsync();
                    SyncAuthorityCrossPairMoveCoordinator.PlanningPhaseForTest = async phase =>
                    {
                        if (!string.Equals(phase, "cross-pair-correlated-before-reservation",
                                StringComparison.Ordinal) ||
                            Interlocked.CompareExchange(ref reservationInvalidated, 1, 0) != 0)
                            return;
                        if (direction == "local-to-dropbox")
                            File.Move(Path.Combine(destination.LocalRoot, "moved.txt"),
                                Path.Combine(destination.LocalRoot, "moved-again.txt"));
                        else
                            remotes[destination.Id].Move("moved.txt", "moved-again.txt");
                        await new SyncAuthorityObservationCoordinator(destination, remotes[destination.Id], authority,
                                await authority.LoadActiveRootBindingIdAsync(destination.Id, "local"),
                                await authority.LoadActiveRootBindingIdAsync(destination.Id, "dropbox"))
                            .ObserveAsync("monitor");
                    };
                }
                else if (scenario.Name == "during-observation-gap")
                {
                    if (direction == "local-to-dropbox")
                        remotes[destination.Id].BeforeListEntriesForTest = _ => InjectMoveCoreAsync();
                    else
                        remotes[destination.Id].AfterListEntriesForTest = () =>
                            InjectMoveCoreAsync().GetAwaiter().GetResult();
                }
                else if (scenario.Name == "during-observation-duplicate")
                {
                    if (direction == "local-to-dropbox")
                        remotes[source.Id].BeforeListEntriesForTest = _ => InjectMoveCoreAsync();
                    else
                        remotes[source.Id].AfterListEntriesForTest = () =>
                            InjectMoveCoreAsync().GetAwaiter().GetResult();
                }
                else if (coordinated)
                    host.CoordinatedPlanningPhaseForTest = async phase =>
                    {
                        if (string.Equals(phase, scenario.Phase, StringComparison.Ordinal))
                            await InjectMoveCoreAsync();
                    };
                if (coordinated && scenario.Name == "after-ordinary-planning")
                    host.CoordinatedWorkerStartedForTest = async activeWorkers =>
                    {
                        int prior;
                        do
                        {
                            prior = Volatile.Read(ref observedWorkers);
                            if (prior >= activeWorkers) break;
                        } while (Interlocked.CompareExchange(ref observedWorkers, activeWorkers, prior) != prior);
                        if (activeWorkers >= 2) twoWorkersReached.TrySetResult(true);
                        await twoWorkersReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    };

                var sourceUploadBefore = remotes[source.Id].UploadCount;
                var destinationUploadBefore = remotes[destination.Id].UploadCount;
                var sourceDownloadBefore = remotes[source.Id].DownloadCount;
                var destinationDownloadBefore = remotes[destination.Id].DownloadCount;
                var sourceArchiveBefore = remotes[source.Id].ArchiveCount;
                var destinationArchiveBefore = remotes[destination.Id].ArchiveCount;
                var sourceDeleteBefore = remotes[source.Id].DeleteCount;
                var destinationDeleteBefore = remotes[destination.Id].DeleteCount;
                IReadOnlyList<SyncPair> invokedPairs = scenario.Name == "manual-single-pair" ? [source] : pairs;
                var result = await host.RunPairsAsync(invokedPairs, scenario.Name == "manual-single-pair"
                        ? "Manual"
                        : "Generation race",
                    coordinated ? null : InjectMoveFromProgressAsync);
                True(result.Items.All(item => item.FailureMessage is null));
                True(injected == 1);
                Equal(scenario.Name == "cross-pair-reservation-refresh" ? 1 : 0,
                    reservationInvalidated);
                Equal(scenario.Name == "manual-single-pair" ? "old-independent" : "independent-progress",
                    File.ReadAllText(Path.Combine(unrelated.LocalRoot, scenario.Name == "manual-single-pair"
                        ? "independent.txt"
                        : "progress.txt")));
                if (scenario.Name == "manual-single-pair")
                    True(!File.Exists(Path.Combine(unrelated.LocalRoot, "progress.txt")));

                var fallbackUploads = remotes[source.Id].UploadCount + remotes[destination.Id].UploadCount -
                                      sourceUploadBefore - destinationUploadBefore;
                var fallbackDownloads = remotes[source.Id].DownloadCount + remotes[destination.Id].DownloadCount -
                                        sourceDownloadBefore - destinationDownloadBefore;
                var fallbackArchives = remotes[source.Id].ArchiveCount + remotes[destination.Id].ArchiveCount -
                                       sourceArchiveBefore - destinationArchiveBefore;
                var fallbackDeletes = remotes[source.Id].DeleteCount + remotes[destination.Id].DeleteCount -
                                      sourceDeleteBefore - destinationDeleteBefore;
                var providerMoves = remotes[source.Id].MoveCount - sourceMoveBefore;
                var localSourcePresent = File.Exists(Path.Combine(source.LocalRoot, "moved.txt"));
                var destinationPath = scenario.Name == "cross-pair-reservation-refresh"
                    ? "moved-again.txt"
                    : "moved.txt";
                var localDestinationPresent = File.Exists(Path.Combine(destination.LocalRoot, destinationPath));
                var remoteSourcePresent = remotes[source.Id].Exists("moved.txt");
                var remoteDestinationPresent = remotes[destination.Id].Exists(destinationPath);
                var crossPairOperations = await authority.CountOperationsOfKindForTestAsync(
                    SyncAuthorityOperationKind.CrossPairMove);
                var crossPairStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>())
                    crossPairStatusCounts[status.ToString()] = await authority.CountOperationsWithStatusForTestAsync(
                        SyncAuthorityOperationKind.CrossPairMove, status);
                if (coordinated)
                {
                    Equal(0, fallbackUploads);
                    Equal(0, fallbackDownloads);
                    Equal(0, fallbackArchives);
                    Equal(0, fallbackDeletes);
                    True(!localSourcePresent && localDestinationPresent);
                    True(!remoteSourcePresent && remoteDestinationPresent);
                    // Local-to-Dropbox counts Sync Shark's provider move; Dropbox-to-local
                    // counts the single injected provider move that creates the test condition.
                    Equal(1, providerMoves);
                    Equal(1, crossPairOperations);
                    Equal(1, crossPairStatusCounts[nameof(SyncAuthorityOperationStatus.Completed)]);
                    foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                                 item != SyncAuthorityOperationStatus.Completed))
                        Equal(0, crossPairStatusCounts[status.ToString()]);
                    PilotLocalEntry destinationLocalIdentity;
                    using (var destinationLease = PilotDirectoryLease.Open(destination.LocalRoot,
                               new PilotDirectoryIdentity(destination.LocalVolumeSerialNumber,
                                   destination.LocalRootFileId)))
                        destinationLocalIdentity = (await new PilotLocalScanner().ScanAsync(destinationLease))
                            .Entries[destinationPath];
                    Equal(sourceLocalIdentityBefore.VolumeSerialNumber,
                        destinationLocalIdentity.VolumeSerialNumber);
                    Equal(sourceLocalIdentityBefore.FileId, destinationLocalIdentity.FileId);
                    var destinationRemoteIdentity = (await remotes[destination.Id].ListEntriesAsync())
                        .Single(item => item.RelativePath == destinationPath);
                    Equal("id:generation-stable-object", destinationRemoteIdentity.Id);
                    Equal("move-payload", File.ReadAllText(Path.Combine(destination.LocalRoot, destinationPath)));
                    Equal("move-payload", remotes[destination.Id].ReadText(destinationPath));
                    if (scenario.Name == "after-ordinary-planning") True(observedWorkers >= 2);
                }
                else if (direction == "local-to-dropbox")
                {
                    True(fallbackUploads > 0);
                    True(remoteSourcePresent && remoteDestinationPresent);
                    Equal(0, crossPairOperations);
                }
                else
                {
                    True(fallbackDownloads > 0);
                    True(localSourcePresent && localDestinationPresent);
                    Equal(0, crossPairOperations);
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = coordinated
                        ? "ss_917e_f01_coordinated_observation_set"
                        : "ss_917e_f01_control_disabled_comparison",
                    direction,
                    configuredWorkers = 2,
                    observedWorkers,
                    injectedBoundary = scenario.Phase ?? (coordinated
                        ? scenario.Name
                        : "after_global_correlation_before_pair_planning"),
                    reservationInvalidated,
                    fallbackUploads,
                    fallbackDownloads,
                    fallbackArchives,
                    fallbackDeletes,
                    providerMoves,
                    crossPairOperations,
                    crossPairStatusCounts,
                    localIdentityPreserved = coordinated,
                    providerIdentityPreserved = coordinated,
                    sourcePresent = new { local = localSourcePresent, dropbox = remoteSourcePresent },
                    destinationPresent = new
                    {
                        local = localDestinationPresent,
                        dropbox = remoteDestinationPresent
                    },
                    selectedPairCount = invokedPairs.Count,
                    unrelatedPairOrdinaryEffect = scenario.Name != "manual-single-pair"
                }));
            }
            finally
            {
                SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
                SyncAuthorityCrossPairMoveCoordinator.PlanningPhaseForTest = null;
                host.CoordinatedWorkerStartedForTest = null;
            }
            }

            await CrossPairMonitorEntryPointContract(root);
            await ConcurrentCrossPairMovesContract(root);
            await CrossPairDirectoryDescendantRuntimeContract();
            await CrossPairDirectoryStaleDescendantBeforeRootContract(root);
            await CrossPairDirectoryCollisionContract(root);
            await CrossPairDirectoryProcessCrashAfterEffectContract(root);
            await CrossPairProcessCrashBeforeDispatchContract(root);
            await CrossPairRestartBeforeDispatchContract(root);
            await CrossPairPauseBeforeEffectContract(root);
            await CoordinatedPauseBeforeEffectContract(root);
            await CrossPairReadyAuthorityTransitionContract(root);
        }

        var invalidationRoot = Path.Combine(root, "reservation-invalidation");
        Directory.CreateDirectory(invalidationRoot);
        using (var appLock = PilotInstanceLock.Acquire(Path.Combine(invalidationRoot, "state", "instance.lock")))
        {
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var localRoot = Path.Combine(invalidationRoot, "pair");
            Directory.CreateDirectory(localRoot);
            Write(localRoot, "versioned.txt", "version-1");
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/Generation/invalidation", "id:generation-invalidation-root"),
                "invalidation", connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = false,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:generation-invalidation-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease,
                await pairStore.GetOrCreateInstallationIdAsync());
            await pairStore.UpsertAsync(pair, select: true);
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId("versioned.txt", "version-1", "id:generation-invalidation-file");
            var authority = new SyncAuthorityDatabase(Path.Combine(invalidationRoot, "vNext", "state",
                "syncshark.db"));
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new NoNetworkHttpHandler()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            var pairs = (await host.LoadPairsAsync()).Pairs;
            True((await host.RunPairsAsync(pairs, "Invalidation baseline")).Items
                .All(item => item.FailureMessage is null));
            Write(localRoot, "versioned.txt", "version-2");
            File.SetLastWriteTimeUtc(Path.Combine(localRoot, "versioned.txt"),
                DateTime.UtcNow.AddMinutes(1));
            var invalidated = 0;
            host.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                    Interlocked.CompareExchange(ref invalidated, 1, 0) != 0)
                    return;
                Write(localRoot, "versioned.txt", "version-3");
                File.SetLastWriteTimeUtc(Path.Combine(localRoot, "versioned.txt"),
                    DateTime.UtcNow.AddMinutes(2));
                var localBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "local");
                var dropboxBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
                await new SyncAuthorityObservationCoordinator(pair, remote, authority, localBinding,
                    dropboxBinding).ObserveAsync("monitor");
            };
            var uploadsBefore = remote.UploadCount;
            var result = await host.RunPairsAsync(pairs, "Reservation invalidation");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "coordinated_reservation_invalidation_witness",
                failures = result.Items.Where(item => item.FailureMessage is not null).Select(item => new
                {
                    item.FailureType,
                    item.FailureMessage
                }).ToArray(),
                decisions = result.Items.SelectMany(item => item.Result?.Decisions ?? []).Select(item => new
                {
                    item.RelativePath,
                    kind = item.Kind.ToString(),
                    item.Reason
                }).ToArray(),
                remote = remote.ReadText("versioned.txt"),
                uploads = remote.UploadCount - uploadsBefore,
                superseded = await authority.CountOperationsWithStatusForTestAsync(
                    SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Superseded),
                completed = await authority.CountOperationsWithStatusForTestAsync(
                    SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Completed)
            }));
            True(result.Items.All(item => item.FailureMessage is null));
            Equal(1, invalidated);
            Equal("version-3", remote.ReadText("versioned.txt"));
            Equal(1, remote.UploadCount - uploadsBefore);
            Equal(1, await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Superseded));
            Equal(1, await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Completed));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "coordinated_reservation_invalidation",
                invalidatedUnissuedOperations = 1,
                dispatchedSuccessorOperations = 1,
                uploadedVersion = "version-3"
            }));
        }
        await FinalEffectAdmissionGuardComparisonContract(root);
        await PlanningAuthorityDimensionsContract(root);
        Console.WriteLine(onlyInvalidation
            ? "PASS coordinated planning invalidation witness"
            : "PASS coordinated observation-set cross-pair generation contract");
    });
}

static Task CrossPairDirectoryDescendantRuntimeContract()
{
    return WithTempRoot(async root =>
    {
        foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
        {
            var scenarioRoot = Path.Combine(root, direction);
            Directory.CreateDirectory(scenarioRoot);
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var installationId = await pairStore.GetOrCreateInstallationIdAsync();
            var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

            async Task<SyncPair> CreatePairAsync(string name)
            {
                var localRoot = Path.Combine(scenarioRoot, name);
                Directory.CreateDirectory(localRoot);
                var remote = new FakeSyncRemoteStore();
                if (name == "source")
                {
                    Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                    Write(localRoot, "moved/root.txt", "root-payload");
                    Write(localRoot, "moved/nested/child.txt", "child-payload");
                    remote.PutDirectory("moved");
                    remote.PutDirectory("moved/nested");
                    remote.PutWithId("moved/root.txt", "root-payload", "id:directory-root-file");
                    remote.PutWithId("moved/nested/child.txt", "child-payload", "id:directory-child-file");
                }
                else
                {
                    Write(localRoot, name + ".txt", name + "-baseline");
                    remote.PutWithId(name + ".txt", name + "-baseline", "id:" + name + "-baseline");
                }

                using var lease = PilotDirectoryLease.Open(localRoot);
                var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                        lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                        "/DirectoryRuntime/" + direction + "/" + name,
                        "id:directory-runtime-root-" + direction + "-" + name),
                    name, connection.RootNamespaceId) with
                {
                    ConnectionId = connection.ConnectionId,
                    InitialReviewApproved = true,
                    PropagateDeletions = true,
                    RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                        "id:directory-runtime-recovery", connection.RootNamespaceId)
                };
                pair = await BindFixtureControlAsync(pair, lease, installationId);
                await pairStore.UpsertAsync(pair, select: name == "source");
                remotes[pair.Id] = remote;
                return pair;
            }

            var source = await CreatePairAsync("source");
            var destination = await CreatePairAsync("destination");
            var unrelated = await CreatePairAsync("unrelated");
            var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
                "syncshark.db"));
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new NoNetworkHttpHandler()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                (pair, _) => remotes[pair.Id]);
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            var pairs = (await host.LoadPairsAsync()).Pairs;
            True((await host.RunPairsAsync(pairs, "Directory runtime baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Directory runtime stable baseline")).Items
                .All(item => item.FailureMessage is null));

            var paths = new[] { "moved", "moved/nested", "moved/root.txt", "moved/nested/child.txt" };
            Dictionary<string, PilotLocalEntry> sourceLocalBefore;
            using (var sourceLease = PilotDirectoryLease.Open(source.LocalRoot,
                       new PilotDirectoryIdentity(source.LocalVolumeSerialNumber, source.LocalRootFileId)))
                sourceLocalBefore = (await new PilotLocalScanner().ScanAsync(sourceLease)).Entries
                    .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var sourceRemoteBefore = (await remotes[source.Id].ListEntriesAsync())
                .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
            var sourcePointsBefore = (await authority.LoadLatestSynchronizationPointsAsync(source.Id))
                .Where(item => item.LocalRelativePath is not null &&
                               paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.LocalRelativePath!, item => item, StringComparer.OrdinalIgnoreCase);
            Equal(paths.Length, sourceLocalBefore.Count);
            Equal(paths.Length, sourceRemoteBefore.Count);
            Equal(paths.Length, sourcePointsBefore.Count);

            if (direction == "local-to-dropbox")
                Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                    Path.Combine(destination.LocalRoot, "moved"));
            else
            {
                var expected = sourceRemoteBefore["moved"];
                await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved");
            }
            remotes[unrelated.Id].PutWithId("progress.txt", "unrelated-progress",
                "id:directory-unrelated-progress");

            var sourceUploadBefore = remotes[source.Id].UploadCount;
            var destinationUploadBefore = remotes[destination.Id].UploadCount;
            var sourceDownloadBefore = remotes[source.Id].DownloadCount;
            var destinationDownloadBefore = remotes[destination.Id].DownloadCount;
            var sourceArchiveBefore = remotes[source.Id].ArchiveCount;
            var destinationArchiveBefore = remotes[destination.Id].ArchiveCount;
            var sourceDeleteBefore = remotes[source.Id].DeleteCount;
            var destinationDeleteBefore = remotes[destination.Id].DeleteCount;
            var sourceDirectoryCreateBefore = remotes[source.Id].DirectoryCreateCount;
            var destinationDirectoryCreateBefore = remotes[destination.Id].DirectoryCreateCount;
            var sourceMoveBefore = remotes[source.Id].MoveCount;
            var observedWorkers = 0;
            var twoWorkersReached = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var rootEffectReached = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var effectOrder = 0;
            var rootEffectOrder = 0;
            var unrelatedEffectOrder = 0;
            var ordinaryDescendantPlans = 0;
            string? crossPairOperationId = null;
            var forbiddenOrdinaryOperationInventory = new List<object>();
            object? effectAdmissionTrace = null;
            host.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-ordinary-planning-before-dispatch",
                        StringComparison.Ordinal)) return;
                foreach (var pair in new[] { source, destination })
                {
                    foreach (var operation in await RetainedReadyPlansForTestAsync(authority, pair.Id))
                    {
                        if (operation.Kind == SyncAuthorityOperationKind.CrossPairMove)
                        {
                            crossPairOperationId ??= operation.OperationId;
                            continue;
                        }
                        var overlappingPaths = operation.Members.Select(member => member.ExpectedPathKey)
                            .Where(path => path is not null &&
                                           (string.Equals(path, "moved", StringComparison.OrdinalIgnoreCase) ||
                                            path.StartsWith("moved/", StringComparison.OrdinalIgnoreCase)))
                            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                        if (overlappingPaths.Length == 0) continue;
                        forbiddenOrdinaryOperationInventory.Add(new
                        {
                            operation.OperationId,
                            kind = operation.Kind.ToString(),
                            status = operation.Status.ToString(),
                            paths = overlappingPaths
                        });
                    }
                }
                ordinaryDescendantPlans = Math.Max(ordinaryDescendantPlans,
                    forbiddenOrdinaryOperationInventory.Count);
            };
            host.CoordinatedWorkerStartedForTest = async activeWorkers =>
            {
                int prior;
                do
                {
                    prior = Volatile.Read(ref observedWorkers);
                    if (prior >= activeWorkers) break;
                } while (Interlocked.CompareExchange(ref observedWorkers, activeWorkers, prior) != prior);
                if (activeWorkers >= 2) twoWorkersReached.TrySetResult(true);
                await twoWorkersReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };
            var expectedRootPhase = direction == "local-to-dropbox"
                ? "dropbox-cross-root-moved-before-receipt"
                : "local-cross-root-moved-before-receipt";
            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = phase =>
            {
                if (!string.Equals(phase, expectedRootPhase, StringComparison.Ordinal)) return;
                rootEffectOrder = Interlocked.Increment(ref effectOrder);
                rootEffectReached.TrySetResult(true);
            };
            SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "cross-pair-effect-admitted-before-physical-move",
                        StringComparison.Ordinal) || crossPairOperationId is null || effectAdmissionTrace is not null)
                    return;
                var admitted = await authority.LoadOperationAsync(crossPairOperationId);
                effectAdmissionTrace = new
                {
                    phase,
                    admitted.OperationId,
                    status = admitted.Status.ToString(),
                    participantCount = admitted.PlanningAuthorities.Count,
                    participants = admitted.PlanningAuthorities.Select(item => new
                    {
                        item.ObservationSetId,
                        item.PairId,
                        item.ConnectionId,
                        item.LocalScanGenerationId,
                        item.DropboxScanGenerationId,
                        item.ObservationContentFingerprint,
                        item.SynchronizationPointSetFingerprint,
                        item.RootAuthorityFingerprint
                    }).OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray()
                };
            };
            remotes[unrelated.Id].BeforeDownloadForTest = async cancellationToken =>
            {
                await rootEffectReached.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                unrelatedEffectOrder = Interlocked.Increment(ref effectOrder);
            };

            try
            {
                var result = await host.RunPairsAsync(pairs, "Directory descendant overlap");
                True(result.Items.All(item => item.FailureMessage is null), JsonSerializer.Serialize(result.Items
                    .Where(item => item.FailureMessage is not null).Select(item => new
                    {
                        item.Pair.Name,
                        item.FailureType,
                        item.FailureMessage
                    })));
            }
            finally
            {
                host.CoordinatedPlanningPhaseForTest = null;
                host.CoordinatedWorkerStartedForTest = null;
                SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
                SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = null;
                remotes[unrelated.Id].BeforeDownloadForTest = null;
            }

            Equal(0, ordinaryDescendantPlans);
            True(observedWorkers >= 2);
            Equal(1, rootEffectOrder);
            Equal(2, unrelatedEffectOrder);
            Equal("unrelated-progress", File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));
            Equal(0, remotes[source.Id].UploadCount + remotes[destination.Id].UploadCount -
                     sourceUploadBefore - destinationUploadBefore);
            Equal(0, remotes[source.Id].DownloadCount + remotes[destination.Id].DownloadCount -
                     sourceDownloadBefore - destinationDownloadBefore);
            Equal(0, remotes[source.Id].ArchiveCount + remotes[destination.Id].ArchiveCount -
                     sourceArchiveBefore - destinationArchiveBefore);
            Equal(0, remotes[source.Id].DeleteCount + remotes[destination.Id].DeleteCount -
                     sourceDeleteBefore - destinationDeleteBefore);
            Equal(0, remotes[source.Id].DirectoryCreateCount + remotes[destination.Id].DirectoryCreateCount -
                     sourceDirectoryCreateBefore - destinationDirectoryCreateBefore);
            Equal(direction == "local-to-dropbox" ? 1 : 0,
                remotes[source.Id].MoveCount - sourceMoveBefore);

            Dictionary<string, PilotLocalEntry> destinationLocalAfter;
            using (var destinationLease = PilotDirectoryLease.Open(destination.LocalRoot,
                       new PilotDirectoryIdentity(destination.LocalVolumeSerialNumber,
                           destination.LocalRootFileId)))
                destinationLocalAfter = (await new PilotLocalScanner().ScanAsync(destinationLease)).Entries
                    .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var destinationRemoteAfter = (await remotes[destination.Id].ListEntriesAsync())
                .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
            var destinationPointsAfter = (await authority.LoadLatestSynchronizationPointsAsync(destination.Id))
                .Where(item => item.LocalRelativePath is not null &&
                               paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.LocalRelativePath!, item => item, StringComparer.OrdinalIgnoreCase);
            Equal(paths.Length, destinationLocalAfter.Count);
            Equal(paths.Length, destinationRemoteAfter.Count);
            Equal(paths.Length, destinationPointsAfter.Count);
            foreach (var path in paths)
            {
                Equal(sourceLocalBefore[path].VolumeSerialNumber, destinationLocalAfter[path].VolumeSerialNumber);
                Equal(sourceLocalBefore[path].FileId, destinationLocalAfter[path].FileId);
                Equal(sourceRemoteBefore[path].Id, destinationRemoteAfter[path].Id);
                Equal(sourcePointsBefore[path].ObjectId, destinationPointsAfter[path].ObjectId);
            }
            Equal("root-payload", File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "root.txt")));
            Equal("child-payload", File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "nested",
                "child.txt")));
            Equal("root-payload", remotes[destination.Id].ReadText("moved/root.txt"));
            Equal("child-payload", remotes[destination.Id].ReadText("moved/nested/child.txt"));
            True(!Directory.Exists(Path.Combine(source.LocalRoot, "moved")));
            True(!remotes[source.Id].DirectoryExists("moved"));
            Equal(1, await authority.CountOperationsOfKindForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove));
            Equal(1, await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
            foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                         item != SyncAuthorityOperationStatus.Completed))
                Equal(0, await authority.CountOperationsWithStatusForTestAsync(
                    SyncAuthorityOperationKind.CrossPairMove, status));
            True(crossPairOperationId is not null && effectAdmissionTrace is not null);
            var completedMove = await authority.LoadOperationAsync(crossPairOperationId!);
            Equal(29, completedMove.Members.Count);
            Equal(4, completedMove.SubtreeReservations.Count);
            Equal(4, completedMove.SubtreeReservations.Select(item => item.RootBindingId)
                .Distinct(StringComparer.Ordinal).Count());
            foreach (var reservation in completedMove.SubtreeReservations)
            {
                var expectedPaths = paths.Select(path => string.Equals(path, "moved",
                        StringComparison.OrdinalIgnoreCase)
                        ? reservation.RelativePath
                        : reservation.RelativePath + path["moved".Length..])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var reservedMemberPaths = completedMove.Members.Where(item =>
                        string.Equals(item.RootBindingId, reservation.RootBindingId,
                            StringComparison.Ordinal) && item.ExpectedPathKey is not null &&
                        (string.Equals(item.ExpectedPathKey, reservation.RelativePath,
                             StringComparison.OrdinalIgnoreCase) ||
                         item.ExpectedPathKey.StartsWith(reservation.RelativePath + "/",
                             StringComparison.OrdinalIgnoreCase)))
                    .Select(item => item.ExpectedPathKey!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                True(expectedPaths.SetEquals(reservedMemberPaths), JsonSerializer.Serialize(new
                {
                    reservation,
                    expectedPaths = expectedPaths.Order().ToArray(),
                    reservedMemberPaths = reservedMemberPaths.Order().ToArray()
                }));
            }
            var roleCounts = completedMove.Members.GroupBy(item => item.Role, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
            Equal(8, roleCounts["source"]);
            Equal(16, roleCounts["lock"]);
            Equal(4, roleCounts["affected"]);
            Equal(1, roleCounts["destination"]);
            Equal(completedMove.Members.Count,
                completedMove.Members.Select(item => item.MemberId).Distinct(StringComparer.Ordinal).Count());
            Equal(8, completedMove.Members.Count(item => item.Role == "source"));
            Equal(4, completedMove.Members.Where(item => item.Role == "source")
                .Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).Count());
            Equal(0, forbiddenOrdinaryOperationInventory.Count);
            var recoveryArtifactInventory = (await authority.LoadRecoveryArtifactsAsync())
                .Where(item => item.OperationId == completedMove.OperationId)
                .Select(item => new { item.RecoveryArtifactId, item.OperationId, item.Side })
                .OrderBy(item => item.RecoveryArtifactId, StringComparer.Ordinal).ToArray();
            Equal(0, recoveryArtifactInventory.Length);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "cross_pair_directory_descendant_full_runtime",
                direction,
                deletionEnabled = true,
                populatedDescendants = paths.Length - 1,
                observedWorkers,
                adverseOrdering = "root-first",
                rootEffectOrder,
                unrelatedEffectOrder,
                ordinaryDescendantPlans,
                forbiddenOrdinaryOperationInventory,
                rootMoveEffects = 1,
                fallbackUploads = 0,
                fallbackDownloads = 0,
                fallbackArchives = 0,
                fallbackDeletes = 0,
                fallbackDirectoryCreates = 0,
                logicalIdentitiesPreserved = paths.Length,
                ntfsIdentitiesPreserved = paths.Length,
                providerIdentitiesPreserved = paths.Length,
                retainedSubtreePrefixCount = completedMove.SubtreeReservations.Count,
                immutableBindingPathSetClosed = true,
                unrelatedPairProgressed = true,
                operationMemberCount = completedMove.Members.Count,
                operationMemberRoleCounts = roleCounts,
                operationMemberInventory = completedMove.Members.Select(item => new
                {
                    item.MemberId,
                    item.PairId,
                    item.ObjectId,
                    item.Role,
                    item.RootBindingId,
                    item.ExpectedPathKey,
                    present = item.ObjectVersionId is not null
                }).OrderBy(item => item.MemberId, StringComparer.Ordinal).ToArray(),
                recoveryArtifactInventory,
                effectAdmissionTrace
            }));
        }
    });
}

static async Task CrossPairMonitorEntryPointContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "monitor-entry-point-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

        async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content,
            string providerIdentity)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            Write(localRoot, relativePath, content);
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/MonitorEntryPoint/" + direction + "/" + name,
                    "id:monitor-entry-root-" + direction + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = false
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId(relativePath, content, providerIdentity);
            remotes[pair.Id] = remote;
            return pair;
        }

        var source = await CreatePairAsync("source", "moved.txt", "monitor-move-payload",
            "id:monitor-entry-object-" + direction);
        var destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
            "id:monitor-entry-anchor-" + direction);
        var pulse = await CreatePairAsync("pulse", "stable.txt", "stable-pulse",
            "id:monitor-entry-pulse-" + direction);
        var pairs = new[] { source, destination, pulse };
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync(pairs, "Monitor entry baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync(pairs, "Monitor entry stable baseline")).Items
            .All(item => item.FailureMessage is null));

        var callbackStarted = 0;
        var callbackResult = new TaskCompletionSource<SyncAuthorityBatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorStates = await host.ConfigureMonitorsAsync(pairs, async () =>
        {
            if (Interlocked.CompareExchange(ref callbackStarted, 1, 0) != 0) return true;
            try
            {
                callbackResult.TrySetResult(await host.RunPairsAsync(pairs, "Monitor"));
            }
            catch (Exception exception)
            {
                callbackResult.TrySetException(exception);
            }
            return true;
        });
        True(monitorStates.Count == pairs.Length && monitorStates.All(item => item.State == "Monitoring"));

        var sourceUploadsBefore = remotes[source.Id].UploadCount;
        var destinationUploadsBefore = remotes[destination.Id].UploadCount;
        var sourceDownloadsBefore = remotes[source.Id].DownloadCount;
        var destinationDownloadsBefore = remotes[destination.Id].DownloadCount;
        var sourceMovesBefore = remotes[source.Id].MoveCount;
        try
        {
            if (direction == "local-to-dropbox")
            {
                File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
                    Path.Combine(destination.LocalRoot, "moved.txt"));
            }
            else
            {
                var expected = (await remotes[source.Id].ListEntriesAsync())
                    .Single(item => item.RelativePath == "moved.txt");
                await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved.txt");
                Write(pulse.LocalRoot, "monitor-pulse.txt", "monitor-pulse");
            }

            var result = await callbackResult.Task.WaitAsync(TimeSpan.FromSeconds(20));
            True(result.Items.All(item => item.FailureMessage is null));
        }
        finally
        {
            await host.StopMonitorsAsync();
        }

        var fallbackUploads = remotes[source.Id].UploadCount + remotes[destination.Id].UploadCount -
                              sourceUploadsBefore - destinationUploadsBefore;
        var fallbackDownloads = remotes[source.Id].DownloadCount + remotes[destination.Id].DownloadCount -
                                sourceDownloadsBefore - destinationDownloadsBefore;
        Equal(1, callbackStarted);
        Equal(0, fallbackUploads);
        Equal(0, fallbackDownloads);
        Equal(1, remotes[source.Id].MoveCount - sourceMovesBefore);
        True(!File.Exists(Path.Combine(source.LocalRoot, "moved.txt")));
        Equal("monitor-move-payload", File.ReadAllText(Path.Combine(destination.LocalRoot, "moved.txt")));
        True(!remotes[source.Id].Exists("moved.txt"));
        Equal("monitor-move-payload", remotes[destination.Id].ReadText("moved.txt"));
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                     item != SyncAuthorityOperationStatus.Completed))
            Equal(0, await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_monitor_entry_point",
            direction,
            callbackStarted,
            fallbackUploads,
            fallbackDownloads,
            completedMoves = 1,
            allOtherCrossPairStatuses = 0
        }));
    }
}

static async Task ConcurrentCrossPairMovesContract(string root)
{
    var scenarioRoot = Path.Combine(root, "concurrent-independent-cross-pair-moves");
    Directory.CreateDirectory(scenarioRoot);
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
    var pairStore = new SyncPairStore(appLock.StateAuthority);
    var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
    var connection = Binding().Normalize();
    await connectionStore.SaveNewAsync(connection);
    var installationId = await pairStore.GetOrCreateInstallationIdAsync();
    var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

    async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content, string providerId)
    {
        var localRoot = Path.Combine(scenarioRoot, name);
        Directory.CreateDirectory(localRoot);
        Write(localRoot, relativePath, content);
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                "/ConcurrentMoves/" + name, "id:concurrent-move-root-" + name),
            name, connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = false
        };
        pair = await BindFixtureControlAsync(pair, lease, installationId);
        await pairStore.UpsertAsync(pair, select: name == "source-a");
        var remote = new FakeSyncRemoteStore();
        remote.PutWithId(relativePath, content, providerId);
        remotes[pair.Id] = remote;
        return pair;
    }

    var sourceA = await CreatePairAsync("source-a", "move-a.txt", "payload-a", "id:concurrent-a");
    var destinationA = await CreatePairAsync("destination-a", "anchor-a.txt", "anchor-a",
        "id:concurrent-anchor-a");
    var sourceB = await CreatePairAsync("source-b", "move-b.txt", "payload-b", "id:concurrent-b");
    var destinationB = await CreatePairAsync("destination-b", "anchor-b.txt", "anchor-b",
        "id:concurrent-anchor-b");
    var pairs = new[] { sourceA, destinationA, sourceB, destinationB };
    var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
    using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
        new HttpClient(new NoNetworkHttpHandler()), pairStore,
        new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
        new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
        (pair, _) => remotes[pair.Id]);
    await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
    True((await host.RunPairsAsync(pairs, "Concurrent move baseline")).Items
        .All(item => item.FailureMessage is null));
    True((await host.RunPairsAsync(pairs, "Concurrent move stable baseline")).Items
        .All(item => item.FailureMessage is null));

    File.Move(Path.Combine(sourceA.LocalRoot, "move-a.txt"),
        Path.Combine(destinationA.LocalRoot, "move-a.txt"));
    File.Move(Path.Combine(sourceB.LocalRoot, "move-b.txt"),
        Path.Combine(destinationB.LocalRoot, "move-b.txt"));
    var uploadsBefore = remotes.Values.Sum(item => item.UploadCount);
    var downloadsBefore = remotes.Values.Sum(item => item.DownloadCount);
    var archivesBefore = remotes.Values.Sum(item => item.ArchiveCount);
    var deletesBefore = remotes.Values.Sum(item => item.DeleteCount);
    var sourceMovesBefore = remotes[sourceA.Id].MoveCount + remotes[sourceB.Id].MoveCount;
    var observedWorkers = 0;
    var twoWorkersReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var effectAdmissions = 0;
    var concurrentOperationIds = new HashSet<string>(StringComparer.Ordinal);
    var twoEffectAdmissionsReached = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    host.CoordinatedPlanningPhaseForTest = async phase =>
    {
        if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal)) return;
        foreach (var pair in new[] { sourceA, sourceB })
        // This hook precedes group activation. The production runnable queue
        // correctly excludes these sealed-but-not-yet-dispatched reservations.
        // Inspect the retained Ready plans without changing admission authority.
        foreach (var reserved in await ReservedMovesAsync(authority, pair.Id))
            concurrentOperationIds.Add(reserved.OperationId);
    };
    host.CoordinatedWorkerStartedForTest = async activeWorkers =>
    {
        int prior;
        do
        {
            prior = Volatile.Read(ref observedWorkers);
            if (prior >= activeWorkers) break;
        } while (Interlocked.CompareExchange(ref observedWorkers, activeWorkers, prior) != prior);
        if (activeWorkers >= 2) twoWorkersReached.TrySetResult(true);
        await twoWorkersReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    };
    SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = async phase =>
    {
        Equal("cross-pair-effect-admitted-before-physical-move", phase);
        if (Interlocked.Increment(ref effectAdmissions) >= 2)
            twoEffectAdmissionsReached.TrySetResult(true);
        await twoEffectAdmissionsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    };
    try
    {
        var result = await host.RunPairsAsync(pairs, "Concurrent independent moves");
        True(result.Items.All(item => item.FailureMessage is null));
    }
    finally
    {
        host.CoordinatedPlanningPhaseForTest = null;
        host.CoordinatedWorkerStartedForTest = null;
        SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = null;
    }

    True(observedWorkers >= 2);
    Equal(uploadsBefore, remotes.Values.Sum(item => item.UploadCount));
    Equal(downloadsBefore, remotes.Values.Sum(item => item.DownloadCount));
    Equal(archivesBefore, remotes.Values.Sum(item => item.ArchiveCount));
    Equal(deletesBefore, remotes.Values.Sum(item => item.DeleteCount));
    Equal(2, remotes[sourceA.Id].MoveCount + remotes[sourceB.Id].MoveCount - sourceMovesBefore);
    True(!remotes[sourceA.Id].Exists("move-a.txt") && !remotes[sourceB.Id].Exists("move-b.txt"));
    Equal("payload-a", remotes[destinationA.Id].ReadText("move-a.txt"));
    Equal("payload-b", remotes[destinationB.Id].ReadText("move-b.txt"));
    Equal("payload-a", File.ReadAllText(Path.Combine(destinationA.LocalRoot, "move-a.txt")));
    Equal("payload-b", File.ReadAllText(Path.Combine(destinationB.LocalRoot, "move-b.txt")));
    Equal(2, concurrentOperationIds.Count);
    var concurrentOperationInventory = new List<object>();
    foreach (var operationId in concurrentOperationIds.Order(StringComparer.Ordinal))
    {
        var operation = await authority.LoadOperationAsync(operationId);
        concurrentOperationInventory.Add(new
        {
            operation.OperationId,
            kind = operation.Kind.ToString(),
            status = operation.Status.ToString(),
            attempts = operation.Attempts.Count
        });
    }
    var concurrentStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>())
        concurrentStatusCounts[status.ToString()] =
            await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status);
    Equal(2, concurrentStatusCounts[nameof(SyncAuthorityOperationStatus.Completed)]);
    foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                 item != SyncAuthorityOperationStatus.Completed))
        Equal(0, concurrentStatusCounts[status.ToString()]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        metric = "concurrent_independent_cross_pair_moves",
        configuredWorkers = 2,
        observedWorkers,
        effectAdmissions,
        operationStatusInventory = concurrentOperationInventory,
        crossPairStatusCounts = concurrentStatusCounts,
        providerMoveEffects = remotes[sourceA.Id].MoveCount + remotes[sourceB.Id].MoveCount - sourceMovesBefore,
        fallbackUploads = remotes.Values.Sum(item => item.UploadCount) - uploadsBefore,
        fallbackDownloads = remotes.Values.Sum(item => item.DownloadCount) - downloadsBefore,
        archiveOrDeleteEffects = remotes.Values.Sum(item => item.ArchiveCount) - archivesBefore +
                                 remotes.Values.Sum(item => item.DeleteCount) - deletesBefore
    }));
}

static async Task<Dictionary<string, FakeSyncRemoteStore>> CreateCrossPairCrashProbeRemotesAsync(
    IReadOnlyList<SyncPair> pairs, string direction, string destinationPath = "moved.txt")
{
    var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
        StringComparer.Ordinal);
    var source = pairs.Single(item => item.Name == "source");
    var destination = pairs.Single(item => item.Name == "destination");
    var unrelated = pairs.Single(item => item.Name == "unrelated");
    remotes[source.Id].PutWithId("moved.txt", "process-crash-payload",
        "id:process-crash-stable-object");
    remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
        "id:process-crash-destination-anchor");
    remotes[unrelated.Id].PutWithId("unrelated.txt", "unrelated-stable",
        "id:process-crash-unrelated");
    if (direction == "dropbox-to-local")
    {
        var expected = (await remotes[source.Id].ListEntriesAsync())
            .Single(item => item.RelativePath == "moved.txt");
        await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved.txt");
        if (destinationPath != "moved.txt")
            remotes[destination.Id].Move("moved.txt", destinationPath);
    }
    return remotes;
}

static Dictionary<string, FakeSyncRemoteStore> CreateCrossPairDirectoryCrashProbeRemotes(
    IReadOnlyList<SyncPair> pairs, bool effectApplied)
{
    var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
        StringComparer.Ordinal);
    var source = pairs.Single(item => item.Name == "source");
    var destination = pairs.Single(item => item.Name == "destination");
    var unrelated = pairs.Single(item => item.Name == "unrelated");
    var movedRoot = effectApplied ? remotes[destination.Id] : remotes[source.Id];
    movedRoot.PutDirectoryWithId("moved", "id:directory-crash-root");
    movedRoot.PutDirectoryWithId("moved/nested", "id:directory-crash-nested");
    movedRoot.PutWithId("moved/root.txt", "directory-crash-root-payload",
        "id:directory-crash-root-file");
    movedRoot.PutWithId("moved/nested/child.txt", "directory-crash-child-payload",
        "id:directory-crash-child-file");
    remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
        "id:directory-crash-destination-anchor");
    remotes[unrelated.Id].PutWithId("unrelated.txt", "unrelated-stable",
        "id:directory-crash-unrelated");
    return remotes;
}

static async Task RunCrossPairDirectoryEffectCrashProbeAsync(string direction, string scenarioRoot)
{
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
    var pairStore = new SyncPairStore(appLock.StateAuthority);
    var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
    var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
    var retained = pairStore.Load();
    var remotes = CreateCrossPairDirectoryCrashProbeRemotes(retained.Pairs,
        effectApplied: direction == "dropbox-to-local");
    using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
        new HttpClient(new NoNetworkHttpHandler()), pairStore,
        new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
        new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
        (pair, _) => remotes[pair.Id]);
    await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
    var expectedPhase = direction == "local-to-dropbox"
        ? "dropbox-cross-root-moved-before-receipt"
        : "local-cross-root-moved-before-receipt";
    SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = phase =>
    {
        if (string.Equals(phase, expectedPhase, StringComparison.Ordinal))
            NativeTestFiles.TerminateCurrentProcess(215);
    };
    try
    {
        _ = await host.RunPairsAsync(retained.Pairs, "Populated directory crash after root effect");
    }
    finally
    {
        SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
    }
    throw new InvalidDataException(
        "The populated-directory effect crash probe returned past forced termination.");
}

static async Task CrossPairDirectoryProcessCrashAfterEffectContract(string root)
{
    var paths = new[] { "moved", "moved/nested", "moved/root.txt", "moved/nested/child.txt" };
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "directory-effect-crash-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        SyncPair source;
        SyncPair destination;
        SyncPair unrelated;
        Dictionary<string, PilotLocalEntry> sourceLocalBefore;
        Dictionary<string, PilotRemoteEntry> sourceRemoteBefore;
        Dictionary<string, SyncAuthoritySynchronizationPoint> sourcePointsBefore;

        using (var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock")))
        {
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var installationId = await pairStore.GetOrCreateInstallationIdAsync();

            async Task<SyncPair> CreatePairAsync(string name)
            {
                var localRoot = Path.Combine(scenarioRoot, name);
                Directory.CreateDirectory(localRoot);
                if (name == "source")
                {
                    Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                    Write(localRoot, "moved/root.txt", "directory-crash-root-payload");
                    Write(localRoot, "moved/nested/child.txt", "directory-crash-child-payload");
                }
                else if (name == "destination")
                {
                    Write(localRoot, "anchor.txt", "destination-anchor");
                }
                else
                {
                    Write(localRoot, "unrelated.txt", "unrelated-stable");
                }

                using var lease = PilotDirectoryLease.Open(localRoot);
                var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                        lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                        "/DirectoryEffectCrash/" + direction + "/" + name,
                        "id:directory-effect-crash-root-" + direction + "-" + name),
                    name, connection.RootNamespaceId) with
                {
                    ConnectionId = connection.ConnectionId,
                    InitialReviewApproved = true,
                    PropagateDeletions = true,
                    RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                        "id:directory-effect-crash-recovery", connection.RootNamespaceId)
                };
                pair = await BindFixtureControlAsync(pair, lease, installationId);
                await pairStore.UpsertAsync(pair, select: name == "source");
                return pair;
            }

            source = await CreatePairAsync("source");
            destination = await CreatePairAsync("destination");
            unrelated = await CreatePairAsync("unrelated");
            var pairs = new[] { source, destination, unrelated };
            var remotes = CreateCrossPairDirectoryCrashProbeRemotes(pairs, effectApplied: false);
            var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
                "syncshark.db"));
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new NoNetworkHttpHandler()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                (pair, _) => remotes[pair.Id]);
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            True((await host.RunPairsAsync(pairs, "Directory effect crash baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Directory effect crash stable baseline")).Items
                .All(item => item.FailureMessage is null));

            using (var sourceLease = PilotDirectoryLease.Open(source.LocalRoot,
                       new PilotDirectoryIdentity(source.LocalVolumeSerialNumber, source.LocalRootFileId)))
                sourceLocalBefore = (await new PilotLocalScanner().ScanAsync(sourceLease)).Entries
                    .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            sourceRemoteBefore = (await remotes[source.Id].ListEntriesAsync())
                .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
            sourcePointsBefore = (await authority.LoadLatestSynchronizationPointsAsync(source.Id))
                .Where(item => item.LocalRelativePath is not null &&
                               paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.LocalRelativePath!, item => item,
                    StringComparer.OrdinalIgnoreCase);
            Equal(paths.Length, sourceLocalBefore.Count);
            Equal(paths.Length, sourceRemoteBefore.Count);
            Equal(paths.Length, sourcePointsBefore.Count);
        }

        if (direction == "local-to-dropbox")
            Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                Path.Combine(destination.LocalRoot, "moved"));
        NativeTestFiles.RunCrossPairDirectoryEffectCrashChild(direction, scenarioRoot);
        await ExpireCrossPairDirectoryCrashLeaseForTestAsync(
            Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));

        using var restartedLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var restartedPairStore = new SyncPairStore(restartedLock.StateAuthority);
        var restartedConnectionStore = new DropboxConnectionStore(restartedLock.StateAuthority);
        var retainedPairs = restartedPairStore.Load().Pairs;
        source = retainedPairs.Single(item => item.Name == "source");
        destination = retainedPairs.Single(item => item.Name == "destination");
        unrelated = retainedPairs.Single(item => item.Name == "unrelated");
        var restartedAuthority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        Equal(1, await restartedAuthority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.EffectIssuing));
        var queued = await restartedAuthority.ListRunnableOperationsForPairAsync(source.Id, 500);
        var retainedMove = queued.Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove);
        var interrupted = await restartedAuthority.LoadOperationAsync(retainedMove.OperationId);
        Equal(SyncAuthorityOperationStatus.EffectIssuing, interrupted.Status);
        Equal(1, interrupted.Attempts.Count);
        True(interrupted.Members.All(item => item.PairId == source.Id || item.PairId == destination.Id));
        Equal(paths.Length * 2, interrupted.Members.Count(item => item.Role == "source"));

        var sourceLocalRoot = await restartedAuthority.LoadActiveRootBindingIdAsync(source.Id, "local");
        var sourceDropboxRoot = await restartedAuthority.LoadActiveRootBindingIdAsync(source.Id, "dropbox");
        var destinationLocalRoot = await restartedAuthority.LoadActiveRootBindingIdAsync(destination.Id, "local");
        var destinationDropboxRoot = await restartedAuthority.LoadActiveRootBindingIdAsync(destination.Id,
            "dropbox");
        foreach (var path in paths)
        foreach (var rootBinding in new[]
                 {
                     sourceLocalRoot, sourceDropboxRoot, destinationLocalRoot, destinationDropboxRoot
                 })
            True(interrupted.Members.Any(item => item.RootBindingId == rootBinding &&
                                                 string.Equals(item.ExpectedPathKey, path,
                                                     StringComparison.OrdinalIgnoreCase)),
                $"The retained directory transaction omitted {rootBinding}:{path}.");

        var restartedRemotes = CreateCrossPairDirectoryCrashProbeRemotes(retainedPairs,
            effectApplied: true);
        restartedRemotes[unrelated.Id].PutWithId("progress.txt", "unrelated-progress",
            "id:directory-effect-crash-progress");
        var sourceAndDestination = new[] { source.Id, destination.Id };
        var uploadsBefore = sourceAndDestination.Sum(id => restartedRemotes[id].UploadCount);
        var downloadsBefore = sourceAndDestination.Sum(id => restartedRemotes[id].DownloadCount);
        var archivesBefore = sourceAndDestination.Sum(id => restartedRemotes[id].ArchiveCount);
        var deletesBefore = sourceAndDestination.Sum(id => restartedRemotes[id].DeleteCount);
        var createsBefore = sourceAndDestination.Sum(id => restartedRemotes[id].DirectoryCreateCount);
        var movesBefore = sourceAndDestination.Sum(id => restartedRemotes[id].MoveCount);
        var failures = new List<string>();
        using (var restartedHost = new SyncAuthorityRuntimeHost(restartedLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), restartedPairStore,
                   new SyncRuntimeStateStore(restartedLock.StateAuthority), restartedConnectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), restartedAuthority,
                   (pair, _) => restartedRemotes[pair.Id]))
        {
            await restartedHost.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception =>
            {
                lock (failures) failures.Add(exception.ToString());
            };
            try
            {
                var result = await restartedHost.RunPairsAsync(retainedPairs,
                    "Recover populated directory after root effect");
                True(result.Items.All(item => item.FailureMessage is null),
                    JsonSerializer.Serialize(result.Items.Where(item => item.FailureMessage is not null)));
            }
            finally
            {
                SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
            }
        }

        Equal(0, failures.Count);
        Equal(uploadsBefore, sourceAndDestination.Sum(id => restartedRemotes[id].UploadCount));
        Equal(downloadsBefore, sourceAndDestination.Sum(id => restartedRemotes[id].DownloadCount));
        Equal(archivesBefore, sourceAndDestination.Sum(id => restartedRemotes[id].ArchiveCount));
        Equal(deletesBefore, sourceAndDestination.Sum(id => restartedRemotes[id].DeleteCount));
        Equal(createsBefore, sourceAndDestination.Sum(id => restartedRemotes[id].DirectoryCreateCount));
        Equal(movesBefore, sourceAndDestination.Sum(id => restartedRemotes[id].MoveCount));
        Equal("unrelated-progress", File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));

        Dictionary<string, PilotLocalEntry> destinationLocalAfter;
        using (var destinationLease = PilotDirectoryLease.Open(destination.LocalRoot,
                   new PilotDirectoryIdentity(destination.LocalVolumeSerialNumber,
                       destination.LocalRootFileId)))
            destinationLocalAfter = (await new PilotLocalScanner().ScanAsync(destinationLease)).Entries
                .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var destinationRemoteAfter = (await restartedRemotes[destination.Id].ListEntriesAsync())
            .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
        var destinationPointsAfter = (await restartedAuthority.LoadLatestSynchronizationPointsAsync(
                destination.Id))
            .Where(item => item.LocalRelativePath is not null &&
                           paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.LocalRelativePath!, item => item,
                StringComparer.OrdinalIgnoreCase);
        Equal(paths.Length, destinationLocalAfter.Count);
        Equal(paths.Length, destinationRemoteAfter.Count);
        Equal(paths.Length, destinationPointsAfter.Count);
        foreach (var path in paths)
        {
            Equal(sourceLocalBefore[path].VolumeSerialNumber, destinationLocalAfter[path].VolumeSerialNumber);
            Equal(sourceLocalBefore[path].FileId, destinationLocalAfter[path].FileId);
            Equal(sourceRemoteBefore[path].Id, destinationRemoteAfter[path].Id);
            Equal(sourcePointsBefore[path].ObjectId, destinationPointsAfter[path].ObjectId);
        }
        Equal("directory-crash-root-payload",
            File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "root.txt")));
        Equal("directory-crash-child-payload",
            File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "nested", "child.txt")));
        Equal("directory-crash-root-payload", restartedRemotes[destination.Id].ReadText("moved/root.txt"));
        Equal("directory-crash-child-payload",
            restartedRemotes[destination.Id].ReadText("moved/nested/child.txt"));
        True(!Directory.Exists(Path.Combine(source.LocalRoot, "moved")));
        True(!restartedRemotes[source.Id].DirectoryExists("moved"));
        Equal(1, await restartedAuthority.CountOperationsOfKindForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove));
        Equal(1, await restartedAuthority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                     item != SyncAuthorityOperationStatus.Completed))
            Equal(0, await restartedAuthority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status));
        Equal(0, (await restartedAuthority.LoadRecoveryArtifactsAsync())
            .Count(item => item.OperationId == interrupted.OperationId));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_directory_post_effect_process_recovery",
            direction,
            abruptRecoveryEquivalenceKey = CrossPairAbruptRecoveryEquivalenceKey(direction),
            childExitCode = 215,
            interruptedStatus = "EffectIssuing",
            completedStatus = "Completed",
            retainedAttempts = 1,
            retainedSourceMembers = paths.Length * 2,
            retainedRootPathClaims = paths.Length * 4,
            deletionEnabled = true,
            completedMoves = 1,
            additionalEffectsDuringRecovery = 0,
            additionalRootMoveEffectsAfterRestart = 0,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            fallbackArchives = 0,
            fallbackDeletes = 0,
            fallbackDirectoryCreates = 0,
            unexpectedRecoveryArtifacts = 0,
            logicalIdentitiesPreserved = paths.Length,
            ntfsIdentitiesPreserved = paths.Length,
            providerIdentitiesPreserved = paths.Length,
            unrelatedPairProgressed = true
        }));
    }
}

static async Task ExpireCrossPairDirectoryCrashLeaseForTestAsync(string databasePath)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        "UPDATE lease SET expires_utc='2000-01-01T00:00:00.0000000Z' " +
        "WHERE scope_kind='operation' AND status='active';";
    Equal(1, await command.ExecuteNonQueryAsync());
}

static async Task CrossPairDirectoryStaleDescendantBeforeRootContract(string root)
{
    var paths = new[] { "moved", "moved/nested", "moved/root.txt", "moved/nested/child.txt" };
    foreach (var direction in new[] { "dropbox-to-local", "local-to-dropbox" })
    {
        var scenarioRoot = Path.Combine(root, "directory-descendant-first-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();

        async Task<SyncPair> CreatePairAsync(string name)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            if (name == "source")
            {
                Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                Write(localRoot, "moved/root.txt", "adverse-root-payload");
                Write(localRoot, "moved/nested/child.txt", "adverse-child-payload");
            }
            else if (name == "destination")
            {
                Write(localRoot, "anchor.txt", "destination-anchor");
            }
            else
            {
                Write(localRoot, "unrelated.txt", "unrelated-stable");
            }
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/DirectoryDescendantFirst/" + direction + "/" + name,
                    "id:directory-descendant-first-root-" + direction + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:directory-descendant-first-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            return pair;
        }

        var source = await CreatePairAsync("source");
        var destination = await CreatePairAsync("destination");
        var unrelated = await CreatePairAsync("unrelated");
        var pairs = new[] { source, destination, unrelated };
        var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
            StringComparer.Ordinal);
        remotes[source.Id].PutDirectoryWithId("moved", "id:directory-adverse-root");
        remotes[source.Id].PutDirectoryWithId("moved/nested", "id:directory-adverse-nested");
        remotes[source.Id].PutWithId("moved/root.txt", "adverse-root-payload",
            "id:directory-adverse-root-file");
        remotes[source.Id].PutWithId("moved/nested/child.txt", "adverse-child-payload",
            "id:directory-adverse-child-file");
        remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
            "id:directory-adverse-destination-anchor");
        remotes[unrelated.Id].PutWithId("unrelated.txt", "unrelated-stable",
            "id:directory-adverse-unrelated");
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync(pairs, "Directory adverse baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync(pairs, "Directory adverse stable baseline")).Items
            .All(item => item.FailureMessage is null));

        Dictionary<string, PilotLocalEntry> sourceLocalBefore;
        using (var sourceLease = PilotDirectoryLease.Open(source.LocalRoot,
                   new PilotDirectoryIdentity(source.LocalVolumeSerialNumber, source.LocalRootFileId)))
            sourceLocalBefore = (await new PilotLocalScanner().ScanAsync(sourceLease)).Entries
                .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var sourceRemoteBefore = (await remotes[source.Id].ListEntriesAsync())
            .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
        var sourcePointsBefore = (await authority.LoadLatestSynchronizationPointsAsync(source.Id))
            .Where(item => item.LocalRelativePath is not null &&
                           paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.LocalRelativePath!, item => item,
                StringComparer.OrdinalIgnoreCase);

        if (direction == "local-to-dropbox")
            Write(source.LocalRoot, "moved/nested/child.txt", "temporary-stale-version");
        else
            remotes[source.Id].PutBytes("moved/nested/child.txt",
                Encoding.UTF8.GetBytes("temporary-stale-version"));
        var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
                return;
            pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
        };
        var stagedRun = host.RunPairsAsync(pairs, "Stage stale descendant operation");
        True(await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        True(await host.StopActiveRunAsync());
        True((await stagedRun.WaitAsync(TimeSpan.FromSeconds(10))).Stopped);
        host.CoordinatedPlanningPhaseForTest = null;

        var expectedKind = direction == "local-to-dropbox"
            ? SyncAuthorityOperationKind.UpdateRemote
            : SyncAuthorityOperationKind.UpdateLocal;
        var staleQueueItem = (await authority.ListRunnableOperationsForPairAsync(source.Id, 500))
            .Single(item => item.Kind == expectedKind);
        var staleOperation = await authority.LoadOperationAsync(staleQueueItem.OperationId);
        Equal(SyncAuthorityOperationStatus.Ready, staleOperation.Status);
        Equal(0, staleOperation.Attempts.Count);
        True(staleOperation.Members.Any(item => string.Equals(item.ExpectedPathKey,
            "moved/nested/child.txt", StringComparison.OrdinalIgnoreCase)));

        if (direction == "local-to-dropbox")
        {
            Write(source.LocalRoot, "moved/nested/child.txt", "adverse-child-payload");
            Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                Path.Combine(destination.LocalRoot, "moved"));
        }
        else
        {
            remotes[source.Id].PutWithId("moved/nested/child.txt", "adverse-child-payload",
                sourceRemoteBefore["moved/nested/child.txt"].Id ??
                throw new InvalidDataException("The retained provider identity is absent."));
            var expected = (await remotes[source.Id].ListEntriesAsync())
                .Single(item => item.RelativePath == "moved");
            await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved");
        }
        remotes[unrelated.Id].PutWithId("progress.txt", "unrelated-progress",
            "id:directory-descendant-first-progress-" + direction);

        foreach (var pair in new[] { source, destination })
            await new SyncAuthorityObservationCoordinator(pair, remotes[pair.Id], authority,
                    await authority.LoadActiveRootBindingIdAsync(pair.Id, "local"),
                    await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox"))
                .ObserveAsync("monitor");
        True(staleOperation.PlanningAuthorities.Count > 0);
        var staleAuthorityDetected = false;
        foreach (var planningAuthority in staleOperation.PlanningAuthorities)
            staleAuthorityDetected |= !await authority.IsPlanningAuthorityCurrentAsync(planningAuthority);
        True(staleAuthorityDetected);

        var sourceAndDestination = new[] { source.Id, destination.Id };
        var uploadsBefore = sourceAndDestination.Sum(id => remotes[id].UploadCount);
        var downloadsBefore = sourceAndDestination.Sum(id => remotes[id].DownloadCount);
        var archivesBefore = sourceAndDestination.Sum(id => remotes[id].ArchiveCount);
        var deletesBefore = sourceAndDestination.Sum(id => remotes[id].DeleteCount);
        var createsBefore = sourceAndDestination.Sum(id => remotes[id].DirectoryCreateCount);
        var movesBefore = sourceAndDestination.Sum(id => remotes[id].MoveCount);
        var runtimeFailures = new List<string>();
        SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception => runtimeFailures.Add(
            exception.GetType().Name + ": " + exception.Message);
        SyncAuthorityBatchResult result;
        try
        {
            result = await host.RunPairsAsync(pairs, "Stale descendant before directory root");
        }
        finally
        {
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
        }
        True(result.Items.All(item => item.FailureMessage is null),
            JsonSerializer.Serialize(result.Items.Where(item => item.FailureMessage is not null)));

        var superseded = await authority.LoadOperationAsync(staleOperation.OperationId);
        Equal(SyncAuthorityOperationStatus.Superseded, superseded.Status);
        Equal(0, superseded.Attempts.Count);
        Equal(uploadsBefore, sourceAndDestination.Sum(id => remotes[id].UploadCount));
        Equal(downloadsBefore, sourceAndDestination.Sum(id => remotes[id].DownloadCount));
        Equal(archivesBefore, sourceAndDestination.Sum(id => remotes[id].ArchiveCount));
        Equal(deletesBefore, sourceAndDestination.Sum(id => remotes[id].DeleteCount));
        Equal(createsBefore, sourceAndDestination.Sum(id => remotes[id].DirectoryCreateCount));
        Equal(direction == "local-to-dropbox" ? 1 : 0,
            sourceAndDestination.Sum(id => remotes[id].MoveCount) - movesBefore);
        Equal("unrelated-progress", File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));

        Dictionary<string, PilotLocalEntry> destinationLocalAfter;
        using (var destinationLease = PilotDirectoryLease.Open(destination.LocalRoot,
                   new PilotDirectoryIdentity(destination.LocalVolumeSerialNumber,
                       destination.LocalRootFileId)))
            destinationLocalAfter = (await new PilotLocalScanner().ScanAsync(destinationLease)).Entries
                .Where(item => paths.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var destinationRemoteAfter = (await remotes[destination.Id].ListEntriesAsync())
            .Where(item => paths.Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
        var destinationPointsAfter = (await authority.LoadLatestSynchronizationPointsAsync(destination.Id))
            .Where(item => item.LocalRelativePath is not null &&
                           paths.Contains(item.LocalRelativePath, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.LocalRelativePath!, item => item,
                StringComparer.OrdinalIgnoreCase);
        True(destinationLocalAfter.Count == paths.Length, JsonSerializer.Serialize(new
        {
            direction,
            destinationLocalInventory = Directory.EnumerateFileSystemEntries(destination.LocalRoot, "*",
                    SearchOption.AllDirectories)
                .Select(item => Path.GetRelativePath(destination.LocalRoot, item)).Order().ToArray(),
            sourceLocalInventory = Directory.EnumerateFileSystemEntries(source.LocalRoot, "*",
                    SearchOption.AllDirectories)
                .Select(item => Path.GetRelativePath(source.LocalRoot, item)).Order().ToArray(),
            destinationRemoteInventory = destinationRemoteAfter.Keys.Order().ToArray(),
            runtimeFailures,
            result = result.Items.Select(item => new
            {
                item.Pair.Id,
                item.FailureType,
                item.FailureMessage,
                decisions = item.Result?.Decisions
            }).ToArray()
        }));
        Equal(paths.Length, destinationRemoteAfter.Count);
        Equal(paths.Length, destinationPointsAfter.Count);
        foreach (var path in paths)
        {
            Equal(sourceLocalBefore[path].VolumeSerialNumber, destinationLocalAfter[path].VolumeSerialNumber);
            Equal(sourceLocalBefore[path].FileId, destinationLocalAfter[path].FileId);
            Equal(sourceRemoteBefore[path].Id, destinationRemoteAfter[path].Id);
            Equal(sourcePointsBefore[path].ObjectId, destinationPointsAfter[path].ObjectId);
        }
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(expectedKind,
            SyncAuthorityOperationStatus.Superseded));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_directory_adverse_ordering",
            direction,
            adverseOrdering = "stale-descendant-ready-before-root",
            staleDescendantKind = expectedKind.ToString(),
            staleDescendantStatus = "Superseded",
            staleDescendantAttempts = 0,
            rootMoveCompleted = 1,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            fallbackArchives = 0,
            fallbackDeletes = 0,
            fallbackDirectoryCreates = 0,
            logicalIdentitiesPreserved = paths.Length,
            ntfsIdentitiesPreserved = paths.Length,
            providerIdentitiesPreserved = paths.Length,
            unrelatedPairProgressed = true
        }));
    }
}

static async Task CrossPairDirectoryCollisionContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "directory-collision-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();

        async Task<SyncPair> CreatePairAsync(string name)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            if (name == "source")
            {
                Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                Write(localRoot, "moved/root.txt", "collision-source-root");
                Write(localRoot, "moved/nested/child.txt", "collision-source-child");
            }
            else
                Write(localRoot, "anchor.txt", name + "-anchor");
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/DirectoryCollision/" + direction + "/" + name,
                    "id:directory-collision-root-" + direction + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:directory-collision-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            return pair;
        }

        var source = await CreatePairAsync("source");
        var destination = await CreatePairAsync("destination");
        var unrelated = await CreatePairAsync("unrelated");
        var pairs = new[] { source, destination, unrelated };
        var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
            StringComparer.Ordinal);
        remotes[source.Id].PutDirectoryWithId("moved", "id:directory-collision-source-root");
        remotes[source.Id].PutDirectoryWithId("moved/nested", "id:directory-collision-source-nested");
        remotes[source.Id].PutWithId("moved/root.txt", "collision-source-root",
            "id:directory-collision-source-file");
        remotes[source.Id].PutWithId("moved/nested/child.txt", "collision-source-child",
            "id:directory-collision-source-child");
        remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
            "id:directory-collision-destination-anchor");
        remotes[unrelated.Id].PutWithId("anchor.txt", "unrelated-anchor",
            "id:directory-collision-unrelated-anchor");
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync(pairs, "Directory collision baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync(pairs, "Directory collision stable baseline")).Items
            .All(item => item.FailureMessage is null));

        if (direction == "local-to-dropbox")
        {
            Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                Path.Combine(destination.LocalRoot, "moved"));
            remotes[destination.Id].PutDirectoryWithId("moved",
                "id:directory-collision-occupant-root");
            remotes[destination.Id].PutDirectoryWithId("moved/nested",
                "id:directory-collision-occupant-nested");
            remotes[destination.Id].PutWithId("moved/nested/occupant.txt", "cloud-occupant",
                "id:directory-collision-cloud-occupant");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(destination.LocalRoot, "moved", "nested"));
            Write(destination.LocalRoot, "moved/nested/occupant.txt", "local-occupant");
            var expected = (await remotes[source.Id].ListEntriesAsync())
                .Single(item => item.RelativePath == "moved");
            await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved");
        }
        remotes[unrelated.Id].PutWithId("progress.txt", "collision-unrelated-progress",
            "id:directory-collision-progress-" + direction);

        var sourceAndDestination = new[] { source.Id, destination.Id };
        var uploadsBefore = sourceAndDestination.Sum(id => remotes[id].UploadCount);
        var downloadsBefore = sourceAndDestination.Sum(id => remotes[id].DownloadCount);
        var archivesBefore = sourceAndDestination.Sum(id => remotes[id].ArchiveCount);
        var deletesBefore = sourceAndDestination.Sum(id => remotes[id].DeleteCount);
        var createsBefore = sourceAndDestination.Sum(id => remotes[id].DirectoryCreateCount);
        var movesBefore = sourceAndDestination.Sum(id => remotes[id].MoveCount);
        string? crossPairOperationId = null;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                crossPairOperationId is not null)
                return;
            var reservations = await ReservedMovesAsync(authority, source.Id);
            if (reservations.Count != 0)
                crossPairOperationId = reservations.Single().OperationId;
        };
        SyncAuthorityBatchResult result;
        try
        {
            result = await host.RunPairsAsync(pairs, "Populated directory destination collision");
        }
        finally
        {
            host.CoordinatedPlanningPhaseForTest = null;
        }
        True(result.Items.All(item => item.FailureMessage is null),
            JsonSerializer.Serialize(result.Items.Where(item => item.FailureMessage is not null)));

        Equal(uploadsBefore, sourceAndDestination.Sum(id => remotes[id].UploadCount));
        Equal(downloadsBefore, sourceAndDestination.Sum(id => remotes[id].DownloadCount));
        Equal(archivesBefore, sourceAndDestination.Sum(id => remotes[id].ArchiveCount));
        Equal(deletesBefore, sourceAndDestination.Sum(id => remotes[id].DeleteCount));
        Equal(createsBefore, sourceAndDestination.Sum(id => remotes[id].DirectoryCreateCount));
        Equal(movesBefore, sourceAndDestination.Sum(id => remotes[id].MoveCount));
        Equal("collision-unrelated-progress",
            File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Blocked));
        Equal(0, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        True(crossPairOperationId is not null);
        var blocked = await authority.LoadOperationAsync(crossPairOperationId!);
        Equal(SyncAuthorityOperationStatus.Blocked, blocked.Status);
        Equal(1, blocked.Attempts.Count);
        Equal(0, (await authority.LoadRecoveryArtifactsAsync())
            .Count(item => item.OperationId == blocked.OperationId));

        if (direction == "local-to-dropbox")
        {
            Equal("collision-source-root", remotes[source.Id].ReadText("moved/root.txt"));
            Equal("collision-source-child", remotes[source.Id].ReadText("moved/nested/child.txt"));
            Equal("cloud-occupant", remotes[destination.Id].ReadText("moved/nested/occupant.txt"));
            Equal("collision-source-root",
                File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "root.txt")));
        }
        else
        {
            Equal("collision-source-root",
                File.ReadAllText(Path.Combine(source.LocalRoot, "moved", "root.txt")));
            Equal("collision-source-child",
                File.ReadAllText(Path.Combine(source.LocalRoot, "moved", "nested", "child.txt")));
            Equal("local-occupant",
                File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "nested", "occupant.txt")));
            Equal("collision-source-root", remotes[destination.Id].ReadText("moved/root.txt"));
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_directory_destination_collision",
            direction,
            collision = "populated-destination-subtree",
            operationStatus = "Blocked",
            retainedNoEffectAttempt = 1,
            sourceContentPreserved = true,
            destinationOccupantPreserved = true,
            rootMoveEffectsAfterCollisionObservation = 0,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            fallbackArchives = 0,
            fallbackDeletes = 0,
            fallbackDirectoryCreates = 0,
            unexpectedRecoveryArtifacts = 0,
            unrelatedPairProgressed = true
        }));
    }
}

static async Task<IReadOnlyList<SyncAuthorityOperationSnapshot>> RetainedReadyPlansForTestAsync(
    SyncAuthorityDatabase authority, string pairId)
{
    // Planning hooks precede sealing. Inspect retained intent without promoting
    // it to runnable work or weakening the production admission decision.
    var ids = new List<string>();
    await using (var read = new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = authority.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
          Pooling = false }.ToString()))
    {
        await read.OpenAsync();
        await using var command = read.CreateCommand();
        command.CommandText = "SELECT operation_id FROM operation WHERE pair_id=$pair AND status='ready';";
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
    }
    var operations = new List<SyncAuthorityOperationSnapshot>();
    foreach (var id in ids) operations.Add(await authority.LoadOperationAsync(id));
    return operations;
}

static async Task CrossPairDirectoryPrefixAuthorityContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "directory-prefix-authority-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();

        async Task<SyncPair> CreatePairAsync(string name)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            if (name == "source")
            {
                Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                Write(localRoot, "moved/root.txt", "prefix-root");
                Write(localRoot, "moved/nested/child.txt", "prefix-child");
            }
            else
                Write(localRoot, "anchor.txt", name + "-anchor");
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/DirectoryPrefixAuthority/" + direction + "/" + name,
                    "id:directory-prefix-root-" + direction + "-" + name), name,
                connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:directory-prefix-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            return pair;
        }

        var source = await CreatePairAsync("source");
        var destination = await CreatePairAsync("destination");
        var unrelated = await CreatePairAsync("unrelated");
        var pairs = new[] { source, destination, unrelated };
        var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
            StringComparer.Ordinal);
        remotes[source.Id].PutDirectoryWithId("moved", "id:directory-prefix-source-root");
        remotes[source.Id].PutDirectoryWithId("moved/nested", "id:directory-prefix-source-nested");
        remotes[source.Id].PutWithId("moved/root.txt", "prefix-root",
            "id:directory-prefix-source-file");
        remotes[source.Id].PutWithId("moved/nested/child.txt", "prefix-child",
            "id:directory-prefix-source-child");
        remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
            "id:directory-prefix-destination-anchor");
        remotes[unrelated.Id].PutWithId("anchor.txt", "unrelated-anchor",
            "id:directory-prefix-unrelated-anchor");
        var databasePath = Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db");
        var authority = new SyncAuthorityDatabase(databasePath);
        var affectedPairIds = new[] { source.Id, destination.Id };
        (int Uploads, int Downloads, int Archives, int Deletes, int Moves, int DirectoryCreates) Effects() =>
            (affectedPairIds.Sum(id => remotes[id].UploadCount),
                affectedPairIds.Sum(id => remotes[id].DownloadCount),
                affectedPairIds.Sum(id => remotes[id].ArchiveCount),
                affectedPairIds.Sum(id => remotes[id].DeleteCount),
                affectedPairIds.Sum(id => remotes[id].MoveCount),
                affectedPairIds.Sum(id => remotes[id].DirectoryCreateCount));

        string? operationId = null;
        var injectedAfterReservation = false;
        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                   (pair, _) => remotes[pair.Id]))
        {
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            True((await host.RunPairsAsync(pairs, "Directory prefix baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Directory prefix stable baseline")).Items
                .All(item => item.FailureMessage is null));

            if (direction == "local-to-dropbox")
                Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                    Path.Combine(destination.LocalRoot, "moved"));
            else
            {
                var expected = (await remotes[source.Id].ListEntriesAsync())
                    .Single(item => item.RelativePath == "moved");
                await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved");
            }
            var effectsBefore = Effects();
            host.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-cross-pair-reservations-before-ordinary-planning",
                        StringComparison.Ordinal) || injectedAfterReservation)
                    return;
                operationId = (await RetainedReadyPlansForTestAsync(authority, source.Id))
                    .Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove).OperationId;
                var retained = await authority.LoadOperationAsync(operationId);
                Equal(4, retained.SubtreeReservations.Count);
                if (direction == "local-to-dropbox")
                    Write(destination.LocalRoot, "moved/nested/late-after-reservation.txt", "late-local");
                else
                    remotes[destination.Id].PutWithId("moved/nested/late-after-reservation.txt",
                        "late-dropbox", "id:directory-prefix-late-" + direction);
                remotes[unrelated.Id].PutWithId("first-progress.txt", "first-progress",
                    "id:directory-prefix-first-progress-" + direction);
                injectedAfterReservation = true;
            };
            SyncAuthorityBatchResult blockedResult;
            try
            {
                blockedResult = await host.RunPairsAsync(pairs,
                    "Late descendant after durable subtree reservation");
            }
            finally
            {
                host.CoordinatedPlanningPhaseForTest = null;
            }
            True(blockedResult.Items.All(item => item.FailureMessage is null),
                JsonSerializer.Serialize(blockedResult.Items.Where(item => item.FailureMessage is not null)));
            True(injectedAfterReservation && operationId is not null);
            Equal(effectsBefore, Effects());
        }

        var blocked = await authority.LoadOperationAsync(operationId!);
        Equal(SyncAuthorityOperationStatus.Blocked, blocked.Status);
        Equal(4, blocked.SubtreeReservations.Count);
        Equal(4, blocked.SubtreeReservations.Select(item => item.RootBindingId)
            .Distinct(StringComparer.Ordinal).Count());
        True(blocked.SubtreeReservations.Count(item => item.PairId == source.Id &&
                                                       string.Equals(item.RelativePath, "moved",
                                                           StringComparison.OrdinalIgnoreCase)) == 2);
        True(blocked.SubtreeReservations.Count(item => item.PairId == destination.Id &&
                                                       string.Equals(item.RelativePath, "moved",
                                                           StringComparison.OrdinalIgnoreCase)) == 2);
        var retainedBeforeRestart = (await authority.ListRetainedSubtreeReservationsAsync())
            .Where(item => item.OperationId == operationId).ToArray();
        Equal(4, retainedBeforeRestart.Length);
        True(retainedBeforeRestart.All(item => item.Status == SyncAuthorityOperationStatus.Blocked));

        Directory.CreateDirectory(Path.Combine(destination.LocalRoot, "moved", "nested"));
        Write(destination.LocalRoot, "moved/nested/restart-local-only.txt", "restart-local-only");
        if (!remotes[destination.Id].DirectoryExists("moved"))
            remotes[destination.Id].PutDirectoryWithId("moved",
                "id:directory-prefix-restart-root-" + direction);
        if (!remotes[destination.Id].DirectoryExists("moved/nested"))
            remotes[destination.Id].PutDirectoryWithId("moved/nested",
                "id:directory-prefix-restart-nested-" + direction);
        remotes[destination.Id].PutWithId("moved/nested/restart-dropbox-only.txt",
            "restart-dropbox-only", "id:directory-prefix-restart-dropbox-" + direction);
        remotes[unrelated.Id].PutWithId("restart-progress.txt", "restart-progress",
            "id:directory-prefix-restart-progress-" + direction);
        var effectsBeforeRestartRun = Effects();
        var forbiddenOrdinaryOperations = new List<string>();
        SyncAuthorityBatchResult restartedResult;
        var restartedAuthority = new SyncAuthorityDatabase(databasePath);
        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), restartedAuthority,
                   (pair, _) => remotes[pair.Id]))
        {
            restarted.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal))
                    return;
                foreach (var pair in new[] { source, destination })
                foreach (var queued in await RetainedReadyPlansForTestAsync(restartedAuthority, pair.Id))
                {
                    if (queued.Kind == SyncAuthorityOperationKind.CrossPairMove) continue;
                    var operation = await restartedAuthority.LoadOperationAsync(queued.OperationId);
                    if (operation.Members.Any(item => item.ExpectedPathKey is not null &&
                                                      (string.Equals(item.ExpectedPathKey, "moved",
                                                           StringComparison.OrdinalIgnoreCase) ||
                                                       item.ExpectedPathKey.StartsWith("moved/",
                                                           StringComparison.OrdinalIgnoreCase))))
                        forbiddenOrdinaryOperations.Add(operation.OperationId + ":" + operation.Kind);
                }
            };
            try
            {
                restartedResult = await restarted.RunPairsAsync(pairs,
                    "Blocked subtree prefix after process restart");
            }
            finally
            {
                restarted.CoordinatedPlanningPhaseForTest = null;
            }
        }

        True(restartedResult.Items.All(item => item.FailureMessage is null),
            JsonSerializer.Serialize(restartedResult.Items.Where(item => item.FailureMessage is not null)));
        Equal(0, forbiddenOrdinaryOperations.Count);
        Equal(effectsBeforeRestartRun, Effects());
        Equal("restart-progress", File.ReadAllText(Path.Combine(unrelated.LocalRoot,
            "restart-progress.txt")));
        var restartedBlocked = await restartedAuthority.LoadOperationAsync(operationId!);
        Equal(SyncAuthorityOperationStatus.Blocked, restartedBlocked.Status);
        Equal(4, (await restartedAuthority.ListRetainedSubtreeReservationsAsync())
            .Count(item => item.OperationId == operationId));
        var destinationPoints = await restartedAuthority.LoadLatestSynchronizationPointsAsync(destination.Id);
        foreach (var unbound in new[]
                 {
                     "moved/nested/late-after-reservation.txt",
                     "moved/nested/restart-local-only.txt",
                     "moved/nested/restart-dropbox-only.txt"
                 })
            True(destinationPoints.All(point =>
                !string.Equals(point.LocalRelativePath, unbound, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(point.DropboxRelativePath, unbound, StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_directory_subtree_prefix_authority",
            direction,
            retainedPrefixCount = restartedBlocked.SubtreeReservations.Count,
            injectedAfterReservation = true,
            blockedAcrossRestart = true,
            localOnlyLaterChildRejected = true,
            dropboxOnlyLaterChildRejected = true,
            forbiddenOrdinaryOperationCount = forbiddenOrdinaryOperations.Count,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            fallbackArchives = 0,
            fallbackDeletes = 0,
            fallbackDirectoryCreates = 0,
            unrelatedPairProgressed = true
        }));
    }
}

static async Task CrossPairDirectoryDivergentDescendantContract(string root)
{
    var cases = new[]
    {
        (Mutation: "matching-add", Timing: "after-root-effect"),
        (Mutation: "edit", Timing: "after-root-effect"),
        (Mutation: "edit", Timing: "before-planning"),
        (Mutation: "add", Timing: "before-planning"),
        (Mutation: "delete", Timing: "before-planning"),
        (Mutation: "edit", Timing: "ready-before-effect")
    };
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    foreach (var testCase in cases)
    {
        var scenarioRoot = Path.Combine(root,
            "directory-divergence-" + direction + "-" + testCase.Mutation + "-" + testCase.Timing);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();

        async Task<SyncPair> CreatePairAsync(string name)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            if (name == "source")
            {
                Directory.CreateDirectory(Path.Combine(localRoot, "moved", "nested"));
                Write(localRoot, "moved/root.txt", "divergence-root");
                Write(localRoot, "moved/nested/child.txt", "divergence-child");
            }
            else
                Write(localRoot, "anchor.txt", name + "-anchor");
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/DirectoryDivergence/" + direction + "/" + testCase.Mutation + "/" +
                    testCase.Timing + "/" + name,
                    "id:directory-divergence-root-" + direction + "-" + testCase.Mutation + "-" +
                    testCase.Timing + "-" + name), name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:directory-divergence-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            return pair;
        }

        var source = await CreatePairAsync("source");
        var destination = await CreatePairAsync("destination");
        var unrelated = await CreatePairAsync("unrelated");
        var pairs = new[] { source, destination, unrelated };
        var remotes = pairs.ToDictionary(item => item.Id, _ => new FakeSyncRemoteStore(),
            StringComparer.Ordinal);
        remotes[source.Id].PutDirectoryWithId("moved", "id:directory-divergence-source-root");
        remotes[source.Id].PutDirectoryWithId("moved/nested", "id:directory-divergence-source-nested");
        remotes[source.Id].PutWithId("moved/root.txt", "divergence-root",
            "id:directory-divergence-source-file");
        remotes[source.Id].PutWithId("moved/nested/child.txt", "divergence-child",
            "id:directory-divergence-source-child");
        remotes[destination.Id].PutWithId("anchor.txt", "destination-anchor",
            "id:directory-divergence-destination-anchor");
        remotes[unrelated.Id].PutWithId("anchor.txt", "unrelated-anchor",
            "id:directory-divergence-unrelated-anchor");
        var databasePath = Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db");
        var authority = new SyncAuthorityDatabase(databasePath);

        string? crossPairOperationId = null;
        var observedCrossPairOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var mutationApplied = false;
        var forbiddenOrdinaryOperationInventory = new List<object>();
        var affectedPairIds = new[] { source.Id, destination.Id };
        (int Uploads, int Downloads, int Archives, int Deletes, int Moves, int DirectoryCreates) effectsBefore;

        void ApplyMutation()
        {
            if (mutationApplied) return;
            if (testCase.Mutation == "matching-add")
            {
                Write(destination.LocalRoot, "moved/nested/matching-unbound.txt",
                    "matching-unbound");
                remotes[destination.Id].PutWithId("moved/nested/matching-unbound.txt",
                    "matching-unbound", "id:directory-divergence-matching-unbound-" + direction);
                mutationApplied = true;
                return;
            }
            if (direction == "local-to-dropbox")
            {
                var child = Path.Combine(destination.LocalRoot, "moved", "nested", "child.txt");
                if (testCase.Mutation == "edit") File.WriteAllText(child, "divergence-child-edited");
                else if (testCase.Mutation == "add")
                    Write(destination.LocalRoot, "moved/nested/added.txt", "divergence-added");
                else File.Delete(child);
            }
            else
            {
                if (testCase.Mutation == "edit")
                    remotes[destination.Id].PutBytes("moved/nested/child.txt",
                        Encoding.UTF8.GetBytes("divergence-child-edited"));
                else if (testCase.Mutation == "add")
                    remotes[destination.Id].PutWithId("moved/nested/added.txt", "divergence-added",
                        "id:directory-divergence-added");
                else remotes[destination.Id].Delete("moved/nested/child.txt");
            }
            mutationApplied = true;
        }

        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                   (pair, _) => remotes[pair.Id]))
        {
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            True((await host.RunPairsAsync(pairs, "Directory divergence baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Directory divergence stable baseline")).Items
                .All(item => item.FailureMessage is null));

            if (direction == "local-to-dropbox")
                Directory.Move(Path.Combine(source.LocalRoot, "moved"),
                    Path.Combine(destination.LocalRoot, "moved"));
            else
            {
                var expected = (await remotes[source.Id].ListEntriesAsync())
                    .Single(item => item.RelativePath == "moved");
                await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved");
            }
            if (testCase.Timing == "before-planning") ApplyMutation();
            remotes[unrelated.Id].PutWithId("progress.txt", "divergence-unrelated-progress",
                "id:directory-divergence-progress-" + direction + "-" + testCase.Mutation + "-" +
                testCase.Timing);
            effectsBefore = (affectedPairIds.Sum(id => remotes[id].UploadCount),
                affectedPairIds.Sum(id => remotes[id].DownloadCount),
                affectedPairIds.Sum(id => remotes[id].ArchiveCount),
                affectedPairIds.Sum(id => remotes[id].DeleteCount),
                affectedPairIds.Sum(id => remotes[id].MoveCount),
                affectedPairIds.Sum(id => remotes[id].DirectoryCreateCount));

            host.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal))
                    return;
                var queuedCrossPairOperations = (await RetainedReadyPlansForTestAsync(authority, source.Id))
                    .Where(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove).ToArray();
                foreach (var queued in queuedCrossPairOperations)
                    observedCrossPairOperationIds.Add(queued.OperationId);
                var firstCrossPairPlanningPhase = crossPairOperationId is null;
                if (firstCrossPairPlanningPhase)
                    crossPairOperationId = queuedCrossPairOperations.Single().OperationId;
                foreach (var pair in new[] { source, destination })
                foreach (var queued in await RetainedReadyPlansForTestAsync(authority, pair.Id))
                {
                    if (queued.Kind == SyncAuthorityOperationKind.CrossPairMove) continue;
                    var operation = await authority.LoadOperationAsync(queued.OperationId);
                    var paths = operation.Members.Select(item => item.ExpectedPathKey)
                        .Where(path => path is not null &&
                                       (string.Equals(path, "moved", StringComparison.OrdinalIgnoreCase) ||
                                        path.StartsWith("moved/", StringComparison.OrdinalIgnoreCase)))
                        .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                    if (paths.Length > 0)
                        forbiddenOrdinaryOperationInventory.Add(new
                        {
                            queued.OperationId,
                            kind = queued.Kind.ToString(),
                            status = queued.Status.ToString(),
                            paths
                        });
                }
                if (firstCrossPairPlanningPhase && testCase.Timing == "ready-before-effect") ApplyMutation();
            };
            var expectedRootPhase = direction == "local-to-dropbox"
                ? "dropbox-cross-root-moved-before-receipt"
                : "local-cross-root-moved-before-receipt";
            SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = phase =>
            {
                if (testCase.Timing == "after-root-effect" &&
                    string.Equals(phase, expectedRootPhase, StringComparison.Ordinal))
                    ApplyMutation();
            };
            SyncAuthorityBatchResult result;
            try
            {
                result = await host.RunPairsAsync(pairs, "Populated directory descendant divergence");
            }
            finally
            {
                host.CoordinatedPlanningPhaseForTest = null;
                SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
            }
            if (testCase.Timing == "after-root-effect")
            {
                var invalidated = result.Items.Where(item => item.FailureMessage is not null).ToArray();
                True(invalidated.All(item => item.FailureType == nameof(InvalidOperationException) &&
                                             item.FailureMessage ==
                                             "The observation set changed before reconciliation planning began."),
                    JsonSerializer.Serialize(invalidated));
                var retry = await host.RunPairsAsync(pairs,
                    "Retry after terminal directory destination divergence");
                True(retry.Items.Single(item => item.Pair.Id == unrelated.Id).FailureMessage is null,
                    JsonSerializer.Serialize(retry.Items.Where(item => item.FailureMessage is not null)));
            }
            else
                True(result.Items.All(item => item.FailureMessage is null),
                    JsonSerializer.Serialize(result.Items.Where(item => item.FailureMessage is not null)));
        }

        True(mutationApplied && crossPairOperationId is not null);
        Equal(0, forbiddenOrdinaryOperationInventory.Count);
        var observedCrossPairOperations = await Task.WhenAll(observedCrossPairOperationIds
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select(item => authority.LoadOperationAsync(item)));
        True(observedCrossPairOperations.Length > 0);
        var expectedRootEffectKind = direction == "local-to-dropbox"
            ? "dropbox-cross-root-native-move"
            : "local-cross-root-move";
        var blocked = testCase.Timing == "after-root-effect"
            ? observedCrossPairOperations.Single(item => item.Attempts.Count == 1 &&
                string.Equals(item.Attempts[0].EffectKind, expectedRootEffectKind, StringComparison.Ordinal))
            : await authority.LoadOperationAsync(crossPairOperationId!);
        True(blocked.Status is SyncAuthorityOperationStatus.Blocked or
                SyncAuthorityOperationStatus.Superseded,
            JsonSerializer.Serialize(observedCrossPairOperations.Select(item => new
            {
                item.OperationId,
                status = item.Status.ToString(),
                attempts = item.Attempts.Select(attempt => new
                    { status = attempt.Status.ToString(), attempt.EffectKind }).ToArray()
            })));
        if (testCase.Timing == "after-root-effect")
        {
            Equal(1, blocked.Attempts.Count);
            Equal(SyncAuthorityAttemptStatus.Blocked, blocked.Attempts[0].Status);
            Equal(expectedRootEffectKind, blocked.Attempts[0].EffectKind);
        }
        else if (blocked.Status == SyncAuthorityOperationStatus.Blocked)
        {
            Equal(1, blocked.Attempts.Count);
            Equal(SyncAuthorityAttemptStatus.Blocked, blocked.Attempts[0].Status);
            Equal("cross-pair-precondition-rejected-no-effect", blocked.Attempts[0].EffectKind);
        }
        else Equal(0, blocked.Attempts.Count);
        var crossPairStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>())
            crossPairStatusCounts[status.ToString()] = await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status);
        Equal(0, crossPairStatusCounts[SyncAuthorityOperationStatus.Completed.ToString()]);
        foreach (var unsafeStatus in new[]
                 {
                     SyncAuthorityOperationStatus.EffectIssuing, SyncAuthorityOperationStatus.EffectUnknown,
                     SyncAuthorityOperationStatus.EffectObserved, SyncAuthorityOperationStatus.Reconciling,
                     SyncAuthorityOperationStatus.Verifying, SyncAuthorityOperationStatus.Committed
                 })
            Equal(0, crossPairStatusCounts[unsafeStatus.ToString()]);
        True(crossPairStatusCounts[SyncAuthorityOperationStatus.Blocked.ToString()] +
             crossPairStatusCounts[SyncAuthorityOperationStatus.Superseded.ToString()] >= 1);
        var effectsAfterBlocked = (affectedPairIds.Sum(id => remotes[id].UploadCount),
            affectedPairIds.Sum(id => remotes[id].DownloadCount),
            affectedPairIds.Sum(id => remotes[id].ArchiveCount),
            affectedPairIds.Sum(id => remotes[id].DeleteCount),
            affectedPairIds.Sum(id => remotes[id].MoveCount),
            affectedPairIds.Sum(id => remotes[id].DirectoryCreateCount));
        var expectedEffectsAfterBlocked = testCase.Timing == "after-root-effect" &&
                                          direction == "local-to-dropbox"
            ? (effectsBefore.Uploads, effectsBefore.Downloads, effectsBefore.Archives,
                effectsBefore.Deletes, effectsBefore.Moves + 1, effectsBefore.DirectoryCreates)
            : effectsBefore;
        Equal(expectedEffectsAfterBlocked, effectsAfterBlocked);
        Equal("divergence-unrelated-progress",
            File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));
        Equal(0, (await authority.LoadRecoveryArtifactsAsync())
            .Count(item => observedCrossPairOperationIds.Contains(item.OperationId)));

        if (direction == "local-to-dropbox")
        {
            if (testCase.Timing == "after-root-effect")
            {
                True(!remotes[source.Id].DirectoryExists("moved"));
                True(remotes[destination.Id].DirectoryExists("moved"));
                Equal("divergence-root", remotes[destination.Id].ReadText("moved/root.txt"));
                Equal("divergence-child", remotes[destination.Id].ReadText("moved/nested/child.txt"));
            }
            else
            {
                True(remotes[source.Id].DirectoryExists("moved"));
                True(!remotes[destination.Id].DirectoryExists("moved"));
                Equal("divergence-root", remotes[source.Id].ReadText("moved/root.txt"));
                Equal("divergence-child", remotes[source.Id].ReadText("moved/nested/child.txt"));
            }
            Equal("divergence-root",
                File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "root.txt")));
            var localChild = Path.Combine(destination.LocalRoot, "moved", "nested", "child.txt");
            if (testCase.Mutation == "edit") Equal("divergence-child-edited", File.ReadAllText(localChild));
            else if (testCase.Mutation == "add")
            {
                Equal("divergence-child", File.ReadAllText(localChild));
                Equal("divergence-added",
                    File.ReadAllText(Path.Combine(destination.LocalRoot, "moved", "nested", "added.txt")));
            }
            else if (testCase.Mutation == "matching-add")
            {
                Equal("divergence-child", File.ReadAllText(localChild));
                Equal("matching-unbound", File.ReadAllText(Path.Combine(destination.LocalRoot,
                    "moved", "nested", "matching-unbound.txt")));
                Equal("matching-unbound", remotes[destination.Id].ReadText(
                    "moved/nested/matching-unbound.txt"));
            }
            else True(!File.Exists(localChild));
        }
        else
        {
            var retainedLocalRoot = testCase.Timing == "after-root-effect"
                ? destination.LocalRoot
                : source.LocalRoot;
            True(Directory.Exists(Path.Combine(retainedLocalRoot, "moved")));
            True(!Directory.Exists(Path.Combine(
                testCase.Timing == "after-root-effect" ? source.LocalRoot : destination.LocalRoot, "moved")));
            Equal("divergence-root", File.ReadAllText(Path.Combine(retainedLocalRoot, "moved", "root.txt")));
            Equal("divergence-child",
                File.ReadAllText(Path.Combine(retainedLocalRoot, "moved", "nested", "child.txt")));
            Equal("divergence-root", remotes[destination.Id].ReadText("moved/root.txt"));
            if (testCase.Mutation == "edit")
                Equal("divergence-child-edited", remotes[destination.Id].ReadText("moved/nested/child.txt"));
            else if (testCase.Mutation == "add")
            {
                Equal("divergence-child", remotes[destination.Id].ReadText("moved/nested/child.txt"));
                Equal("divergence-added", remotes[destination.Id].ReadText("moved/nested/added.txt"));
            }
            else if (testCase.Mutation == "matching-add")
            {
                Equal("divergence-child", remotes[destination.Id].ReadText("moved/nested/child.txt"));
                Equal("matching-unbound", remotes[destination.Id].ReadText(
                    "moved/nested/matching-unbound.txt"));
                Equal("matching-unbound", File.ReadAllText(Path.Combine(destination.LocalRoot,
                    "moved", "nested", "matching-unbound.txt")));
            }
            else True(!remotes[destination.Id].Exists("moved/nested/child.txt"));
        }

        if (testCase.Mutation == "matching-add")
        {
            True(blocked.Members.All(member => !string.Equals(member.ExpectedPathKey,
                "moved/nested/matching-unbound.txt", StringComparison.OrdinalIgnoreCase)));
            var destinationPoints = await authority.LoadLatestSynchronizationPointsAsync(destination.Id);
            True(destinationPoints.All(point =>
                !string.Equals(point.LocalRelativePath, "moved/nested/matching-unbound.txt",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(point.DropboxRelativePath, "moved/nested/matching-unbound.txt",
                    StringComparison.OrdinalIgnoreCase)));
        }

        var restartedAuthority = new SyncAuthorityDatabase(databasePath);
        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), restartedAuthority,
                   (pair, _) => remotes[pair.Id]))
        {
            var recovery = await restarted.RecoverPairsAsync((await restarted.LoadPairsAsync()).Pairs);
            True(recovery.Items.All(item => item.FailureMessage is null),
                JsonSerializer.Serialize(recovery.Items.Where(item => item.FailureMessage is not null)));
        }
        var restartedBlocked = await restartedAuthority.LoadOperationAsync(blocked.OperationId);
        Equal(blocked.Status, restartedBlocked.Status);
        Equal(blocked.Attempts.Count, restartedBlocked.Attempts.Count);
        var restartedOperationInventory = await Task.WhenAll(observedCrossPairOperationIds
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select(item => restartedAuthority.LoadOperationAsync(item)));
        Equal(observedCrossPairOperations.Length, restartedOperationInventory.Length);
        foreach (var observed in observedCrossPairOperations)
        {
            var restartedObserved = restartedOperationInventory.Single(item => item.OperationId == observed.OperationId);
            Equal(observed.Kind, restartedObserved.Kind);
            Equal(observed.Status, restartedObserved.Status);
            Equal(observed.Attempts.Count, restartedObserved.Attempts.Count);
        }
        Equal(effectsAfterBlocked, (affectedPairIds.Sum(id => remotes[id].UploadCount),
            affectedPairIds.Sum(id => remotes[id].DownloadCount),
            affectedPairIds.Sum(id => remotes[id].ArchiveCount),
            affectedPairIds.Sum(id => remotes[id].DeleteCount),
            affectedPairIds.Sum(id => remotes[id].MoveCount),
            affectedPairIds.Sum(id => remotes[id].DirectoryCreateCount)));

        var roleCounts = blocked.Members.GroupBy(item => item.Role, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_directory_descendant_divergence",
            direction,
            mutation = testCase.Mutation,
            timing = testCase.Timing,
            deletionEnabled = true,
            readyOperationPersisted = testCase.Timing is "ready-before-effect" or "after-root-effect",
            operationStatus = blocked.Status.ToString(),
            restartStatus = restartedBlocked.Status.ToString(),
            retainedNoEffectAttempt = blocked.Attempts.Count,
            attemptStatus = blocked.Attempts.Count == 0 ? "None" : blocked.Attempts[0].Status.ToString(),
            effectKind = blocked.Attempts.Count == 0 ? "none" : blocked.Attempts[0].EffectKind,
            rootMoveEffectsAfterDivergenceObservation = testCase.Timing == "after-root-effect" ? 1 : 0,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            fallbackArchives = 0,
            fallbackDeletes = 0,
            fallbackDirectoryCreates = 0,
            unexpectedRecoveryArtifacts = 0,
            sourceContentPreserved = true,
            divergentContentPreserved = true,
            unrelatedPairProgressed = true,
            forbiddenOrdinaryOperationInventory,
            operationMemberCount = blocked.Members.Count,
            operationMemberRoleCounts = roleCounts,
            crossPairStatusCounts,
            operationStatusInventory = observedCrossPairOperations.Select(item => new
            {
                item.OperationId,
                kind = item.Kind.ToString(),
                status = item.Status.ToString(),
                attemptCount = item.Attempts.Count,
                attemptStatuses = item.Attempts.Select(attempt => attempt.Status.ToString()).ToArray(),
                effectKinds = item.Attempts.Select(attempt => attempt.EffectKind).ToArray()
            }).OrderBy(item => item.OperationId, StringComparer.Ordinal).ToArray(),
            operationMemberInventory = blocked.Members.Select(item => new
            {
                item.MemberId,
                item.PairId,
                item.ObjectId,
                item.Role,
                item.RootBindingId,
                item.ExpectedPathKey,
                present = item.ObjectVersionId is not null
            }).OrderBy(item => item.MemberId, StringComparer.Ordinal).ToArray()
        }));
    }
}

static async Task RunCrossPairGenerationCrashProbeAsync(string direction, string scenarioRoot)
{
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
    var pairStore = new SyncPairStore(appLock.StateAuthority);
    var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
    var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
    var retained = new SyncPairStore(appLock.StateAuthority).Load();
    var remotes = await CreateCrossPairCrashProbeRemotesAsync(retained.Pairs, direction);
    using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
        new HttpClient(new NoNetworkHttpHandler()), pairStore,
        new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
        new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
        (pair, _) => remotes[pair.Id]);
    await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
    host.CoordinatedPlanningPhaseForTest = phase =>
    {
        if (phase == "after-ordinary-planning-before-dispatch")
            NativeTestFiles.TerminateCurrentProcess(214);
        return Task.CompletedTask;
    };
    _ = await host.RunPairsAsync(retained.Pairs, "Process crash before dispatch");
    throw new InvalidDataException("The cross-pair generation crash probe returned past forced termination.");
}

static async Task CrossPairProcessCrashBeforeDispatchContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "process-crash-before-dispatch-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        SyncPair source;
        SyncPair destination;
        SyncPair unrelated;
        using (var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock")))
        {
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var installationId = await pairStore.GetOrCreateInstallationIdAsync();

            async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content,
                string providerIdentity)
            {
                var localRoot = Path.Combine(scenarioRoot, name);
                Directory.CreateDirectory(localRoot);
                Write(localRoot, relativePath, content);
                using var lease = PilotDirectoryLease.Open(localRoot);
                var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                        lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                        "/ProcessCrash/" + direction + "/" + name,
                        "id:process-crash-root-" + direction + "-" + name),
                    name, connection.RootNamespaceId) with
                {
                    ConnectionId = connection.ConnectionId,
                    InitialReviewApproved = true,
                    PropagateDeletions = false
                };
                pair = await BindFixtureControlAsync(pair, lease, installationId);
                await pairStore.UpsertAsync(pair, select: name == "source");
                return pair;
            }

            source = await CreatePairAsync("source", "moved.txt", "process-crash-payload",
                "id:process-crash-stable-object");
            destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
                "id:process-crash-destination-anchor");
            unrelated = await CreatePairAsync("unrelated", "unrelated.txt", "unrelated-stable",
                "id:process-crash-unrelated");
            var pairs = new[] { source, destination, unrelated };
            var remotes = await CreateCrossPairCrashProbeRemotesAsync(pairs, direction: "baseline");
            var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
                "syncshark.db"));
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new NoNetworkHttpHandler()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                (pair, _) => remotes[pair.Id]);
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            True((await host.RunPairsAsync(pairs, "Process crash baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Process crash stable baseline")).Items
                .All(item => item.FailureMessage is null));
        }

        if (direction == "local-to-dropbox")
            File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
                Path.Combine(destination.LocalRoot, "moved.txt"));
        NativeTestFiles.RunCrossPairGenerationCrashChild(direction, scenarioRoot);

        using var restartedLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var restartedPairStore = new SyncPairStore(restartedLock.StateAuthority);
        var restartedConnectionStore = new DropboxConnectionStore(restartedLock.StateAuthority);
        var retainedPairs = restartedPairStore.Load().Pairs;
        source = retainedPairs.Single(item => item.Name == "source");
        destination = retainedPairs.Single(item => item.Name == "destination");
        unrelated = retainedPairs.Single(item => item.Name == "unrelated");
        var restartedAuthority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        Equal(1, await restartedAuthority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Ready));
        var restartedRemotes = await CreateCrossPairCrashProbeRemotesAsync(retainedPairs, direction,
            direction == "dropbox-to-local" ? "moved-again.txt" : "moved.txt");
        if (direction == "local-to-dropbox")
            File.Move(Path.Combine(destination.LocalRoot, "moved.txt"),
                Path.Combine(destination.LocalRoot, "moved-again.txt"));
        var destinationLocalBinding = await restartedAuthority.LoadActiveRootBindingIdAsync(destination.Id,
            "local");
        var destinationDropboxBinding = await restartedAuthority.LoadActiveRootBindingIdAsync(destination.Id,
            "dropbox");
        await new SyncAuthorityObservationCoordinator(destination, restartedRemotes[destination.Id],
                restartedAuthority, destinationLocalBinding, destinationDropboxBinding)
            .ObserveAsync("monitor");

        var uploadsBefore = restartedRemotes.Values.Sum(item => item.UploadCount);
        var downloadsBefore = restartedRemotes.Values.Sum(item => item.DownloadCount);
        var archivesBefore = restartedRemotes.Values.Sum(item => item.ArchiveCount);
        var deletesBefore = restartedRemotes.Values.Sum(item => item.DeleteCount);
        var sourceMovesBefore = restartedRemotes[source.Id].MoveCount;
        var batchFailures = new List<string>();
        using (var restartedHost = new SyncAuthorityRuntimeHost(restartedLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), restartedPairStore,
                   new SyncRuntimeStateStore(restartedLock.StateAuthority), restartedConnectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), restartedAuthority,
                   (pair, _) => restartedRemotes[pair.Id]))
        {
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception =>
            {
                lock (batchFailures) batchFailures.Add(exception.ToString());
            };
            try
            {
                var result = await restartedHost.RunPairsAsync(retainedPairs,
                    "Restart after process crash");
                True(result.Items.All(item => item.FailureMessage is null));
            }
            finally
            {
                SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
            }
        }

        Equal(0, batchFailures.Count);
        Equal(uploadsBefore, restartedRemotes.Values.Sum(item => item.UploadCount));
        Equal(downloadsBefore, restartedRemotes.Values.Sum(item => item.DownloadCount));
        Equal(archivesBefore, restartedRemotes.Values.Sum(item => item.ArchiveCount));
        Equal(deletesBefore, restartedRemotes.Values.Sum(item => item.DeleteCount));
        Equal(direction == "local-to-dropbox" ? 1 : 0,
            restartedRemotes[source.Id].MoveCount - sourceMovesBefore);
        True(!File.Exists(Path.Combine(source.LocalRoot, "moved.txt")));
        True(!File.Exists(Path.Combine(destination.LocalRoot, "moved.txt")));
        Equal("process-crash-payload",
            File.ReadAllText(Path.Combine(destination.LocalRoot, "moved-again.txt")));
        True(!restartedRemotes[source.Id].Exists("moved.txt"));
        True(!restartedRemotes[destination.Id].Exists("moved.txt"));
        Equal("process-crash-payload", restartedRemotes[destination.Id].ReadText("moved-again.txt"));
        Equal("unrelated-stable", File.ReadAllText(Path.Combine(unrelated.LocalRoot, "unrelated.txt")));
        Equal(1, await restartedAuthority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Superseded));
        Equal(1, await restartedAuthority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                     item != SyncAuthorityOperationStatus.Superseded &&
                     item != SyncAuthorityOperationStatus.Completed))
            Equal(0, await restartedAuthority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_process_crash_before_dispatch",
            direction,
            childExitCode = 214,
            staleOperationSuperseded = 1,
            latestRelocationCompleted = 1,
            allOtherCrossPairStatuses = 0,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            archiveOrDeleteEffects = 0,
            unrelatedPairPreserved = true
        }));
    }
}

static async Task CrossPairReadyAuthorityTransitionContract(string root)
{
    var dimensions = new[]
    {
        "destination-content", "synchronization-point-set", "connection-account",
        "root-authority", "configuration-revision", "selection-definition"
    };
    foreach (var dimension in dimensions)
    {
        var scenarioRoot = Path.Combine(root, "cross-pair-ready-authority-" + dimension);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var inactiveConnection = new ProviderConnectionRecord(
            "connection-ready-authority-inactive-" + dimension, "dropbox",
            "account-ready-authority-inactive-" + dimension,
            "root-ready-authority-inactive-" + dimension,
            "home-ready-authority-inactive-" + dimension,
            "Inactive fixture account", "", "basic", "", false,
            connection.ToProviderRecord(active: false).Capabilities);
        await connectionStore.SaveInertConnectionAsync(inactiveConnection);
        var retainedConnections = connectionStore.LoadConnections();
        Equal(2, retainedConnections.Count);
        Equal(1, retainedConnections.Count(item => item.IsActive));
        Equal(connection.ConnectionId, retainedConnections.Single(item => item.IsActive).ConnectionId);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

        async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content, string id)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            Write(localRoot, relativePath, content);
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/ReadyAuthority/" + dimension + "/" + name,
                    "id:ready-authority-root-" + dimension + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = true,
                RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:ready-authority-recovery", connection.RootNamespaceId)
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId(relativePath, content, id);
            remotes[pair.Id] = remote;
            return pair;
        }

        var source = await CreatePairAsync("source", "moved.txt", "authority-source",
            "id:ready-authority-moved-" + dimension);
        var destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
            "id:ready-authority-destination-anchor-" + dimension);
        var unrelated = await CreatePairAsync("unrelated", "unrelated.txt", "unrelated-stable",
            "id:ready-authority-unrelated-" + dimension);
        var pairs = new[] { source, destination, unrelated };
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync(pairs, "Ready authority baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync(pairs, "Ready authority stable baseline")).Items
            .All(item => item.FailureMessage is null));
        File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
            Path.Combine(destination.LocalRoot, "moved.txt"));

        var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
                return;
            pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
        };
        var staging = host.RunPairsAsync(pairs, "Stage cross-pair Ready authority");
        True(await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        True(await host.StopActiveRunAsync());
        True((await staging.WaitAsync(TimeSpan.FromSeconds(10))).Stopped);
        host.CoordinatedPlanningPhaseForTest = null;

        var queueItem = (await ReservedMovesAsync(authority, source.Id))
            .Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove);
        var ready = await authority.LoadOperationAsync(queueItem.OperationId);
        Equal(SyncAuthorityOperationStatus.Ready, ready.Status);
        Equal(0, ready.Attempts.Count);
        Equal(pairs.Length, ready.PlanningAuthorities.Count);
        foreach (var planningAuthority in ready.PlanningAuthorities)
        {
            True(await authority.IsPlanningAuthorityCurrentAsync(planningAuthority));
            Equal(connection.ConnectionId, planningAuthority.ConnectionId);
            True(!string.IsNullOrWhiteSpace(planningAuthority.LocalScanGenerationId));
            True(!string.IsNullOrWhiteSpace(planningAuthority.DropboxScanGenerationId));
            True(!string.IsNullOrWhiteSpace(planningAuthority.ObservationContentFingerprint));
            Equal(64, planningAuthority.ObservationContentFingerprint!.Length);
        }
        var destinationDropboxRootAuthority = await authority.LoadRootBindingAuthorityForTestAsync(
            await authority.LoadActiveRootBindingIdAsync(destination.Id, "dropbox"));
        using var destinationDropboxRootIdentity = JsonDocument.Parse(
            destinationDropboxRootAuthority.IdentityJson);
        Equal(connection.RootNamespaceId,
            destinationDropboxRootIdentity.RootElement.GetProperty("rootNamespaceId").GetString());
        True(!destinationDropboxRootAuthority.IdentityJson.Contains(inactiveConnection.RootNamespaceId,
            StringComparison.Ordinal));

        string? supportedEntryPoint = null;
        bool? supportedEntryPointFenced = null;
        if (dimension == "selection-definition")
        {
            supportedEntryPoint = "SetFolderSelectionAsync";
            var rejected = false;
            try
            {
                _ = await host.SetFolderSelectionAsync(destination.Id, "moved.txt", included: false);
            }
            catch (InvalidOperationException exception) when (string.Equals(exception.Message,
                       "Finish or recover this pair's pending work before saving changes. Its current settings stay in effect until then; your draft is kept.",
                       StringComparison.Ordinal))
            {
                rejected = true;
            }
            supportedEntryPointFenced = rejected;
            True(rejected);
            var retainedAfterSupportedEntry = await authority.LoadOperationAsync(ready.OperationId);
            Equal(SyncAuthorityOperationStatus.Ready, retainedAfterSupportedEntry.Status);
            Equal(0, retainedAfterSupportedEntry.Attempts.Count);
            True((await authority.LoadPairSetAsync()).Pairs.Single(item => item.Id == destination.Id)
                .SelectionRules?.Count == 0);
        }
        else if (dimension is "configuration-revision" or "root-authority")
        {
            supportedEntryPoint = "BindPairAsync";
            var rejected = false;
            try
            {
                _ = await host.BindPairAsync(new SyncPairBindingRequest(destination.Id,
                    destination.Name, destination.LocalRoot,
                    new DropboxFolderEntry(destination.Name, destination.DropboxRoot,
                        destination.DropboxFolderId),
                    new DropboxFolderEntry("Recovery", destination.RecoveryRoot!.RootPath,
                        destination.RecoveryRoot.RootFolderId), destination.Enabled, destination.Priority,
                    destination.PropagateDeletions, destination.FilesOnDemandEnabled));
            }
            catch (InvalidOperationException exception) when (string.Equals(exception.Message,
                       "Finish or recover this pair's pending work before saving changes. Its current settings stay in effect until then; your draft is kept.",
                       StringComparison.Ordinal))
            {
                rejected = true;
            }
            supportedEntryPointFenced = rejected;
            True(rejected);
            var retainedAfterSupportedEntry = await authority.LoadOperationAsync(ready.OperationId);
            Equal(SyncAuthorityOperationStatus.Ready, retainedAfterSupportedEntry.Status);
            Equal(0, retainedAfterSupportedEntry.Attempts.Count);
        }
        else if (dimension == "connection-account")
        {
            supportedEntryPoint = "DropboxConnectionStore.SaveAsync";
            var effectsBeforeSwitch = (remotes.Values.Sum(item => item.UploadCount),
                remotes.Values.Sum(item => item.DownloadCount),
                remotes.Values.Sum(item => item.ArchiveCount),
                remotes.Values.Sum(item => item.DeleteCount),
                remotes.Values.Sum(item => item.DirectoryCreateCount),
                remotes.Values.Sum(item => item.MoveCount));
            var switched = connection with
            {
                AccountId = inactiveConnection.AccountId,
                RootNamespaceId = inactiveConnection.RootNamespaceId,
                HomeNamespaceId = inactiveConnection.HomeNamespaceId,
                ConnectionId = inactiveConnection.ConnectionId,
                DisplayName = inactiveConnection.DisplayName,
                RetainedConnections = [connection.ToProviderRecord(active: false)]
            };
            var rejected = false;
            try { await connectionStore.SaveAsync(switched); }
            catch (InvalidDataException exception) when (exception.Message ==
                "Another account's credential or connection cannot be reused.")
            {
                rejected = true;
            }
            supportedEntryPointFenced = rejected;
            True(rejected);
            Equal(connection.ConnectionId, connectionStore.Load()!.ConnectionId);
            Equal(effectsBeforeSwitch, (remotes.Values.Sum(item => item.UploadCount),
                remotes.Values.Sum(item => item.DownloadCount),
                remotes.Values.Sum(item => item.ArchiveCount),
                remotes.Values.Sum(item => item.DeleteCount),
                remotes.Values.Sum(item => item.DirectoryCreateCount),
                remotes.Values.Sum(item => item.MoveCount)));
            var retainedAfterSupportedEntry = await authority.LoadOperationAsync(ready.OperationId);
            Equal(SyncAuthorityOperationStatus.Ready, retainedAfterSupportedEntry.Status);
            Equal(0, retainedAfterSupportedEntry.Attempts.Count);
        }

        switch (dimension)
        {
            case "destination-content":
                Write(destination.LocalRoot, "moved.txt", "edited-destination-version");
                await new SyncAuthorityObservationCoordinator(destination, remotes[destination.Id], authority,
                        await authority.LoadActiveRootBindingIdAsync(destination.Id, "local"),
                        await authority.LoadActiveRootBindingIdAsync(destination.Id, "dropbox"))
                    .ObserveAsync("monitor");
                break;
            case "synchronization-point-set":
            {
                var point = (await authority.LoadLatestSynchronizationPointsAsync(destination.Id)).Single();
                await authority.AcceptSynchronizationPointAsync(
                    "ready-authority-mutated-point-" + dimension, destination.Id, point.ObjectId,
                    point.LocalObjectVersionId, point.DropboxObjectVersionId, point.LocalMembershipId,
                    point.DropboxMembershipId);
                break;
            }
            case "connection-account":
                await authority.RetainPairDefinitionAsync(destination with
                {
                    ConnectionId = inactiveConnection.ConnectionId
                }, select: false);
                break;
            case "root-authority":
            {
                var rootBindingId = await authority.LoadActiveRootBindingIdAsync(destination.Id, "local");
                var rootAuthority = await authority.LoadRootBindingAuthorityForTestAsync(rootBindingId);
                await authority.RegisterRootBindingAsync(rootBindingId, destination.Id, "local",
                    rootAuthority.Generation, rootAuthority.IdentityJson,
                    rootAuthority.DisplayPath + "-relocated");
                break;
            }
            case "configuration-revision":
            {
                var updated = destination with { PolicyRevision = destination.PolicyRevision + 1 };
                await authority.RegisterPairAsync(updated.Id, updated.PolicyRevision);
                await authority.RetainPairDefinitionAsync(updated, select: false);
                break;
            }
            case "selection-definition":
                await authority.RetainPairDefinitionAsync(destination with
                {
                    SelectionRules = [new SyncSelectionRule("anchor", false, "id:selection-mutated")]
                }, select: false);
                break;
        }

        var invalidatedAuthorities = 0;
        foreach (var planningAuthority in ready.PlanningAuthorities)
            if (!await authority.IsPlanningAuthorityCurrentAsync(planningAuthority)) invalidatedAuthorities++;
        True(invalidatedAuthorities > 0);

        var uploadsBefore = remotes.Values.Sum(item => item.UploadCount);
        var downloadsBefore = remotes.Values.Sum(item => item.DownloadCount);
        var archivesBefore = remotes.Values.Sum(item => item.ArchiveCount);
        var deletesBefore = remotes.Values.Sum(item => item.DeleteCount);
        var createsBefore = remotes.Values.Sum(item => item.DirectoryCreateCount);
        var movesBefore = remotes.Values.Sum(item => item.MoveCount);
        var options = new SyncAuthorityPlannerOptions(source.PolicyRevision,
            "profile-ready-authority-" + dimension,
            source.ControlNamespace?.NamespaceId ?? "local-control-ready-authority",
            "dropbox-control-ready-authority", EnableDeletionEffects: true, EnableMoveEffects: true);
        using var readyPreconditions = JsonDocument.Parse(ready.PreconditionsJson);
        var operationGroupId = readyPreconditions.RootElement.GetProperty("operationGroupId").GetString()
                               ?? throw new InvalidDataException("The Ready cross-pair operation has no group.");
        var plan = new SyncAuthorityCrossPairMovePlan(operationGroupId, ready.OperationId,
            source.Id, destination.Id, "moved.txt", "moved.txt",
            SyncAuthorityCrossPairMoveDirection.LocalToDropbox, PilotEntryKind.File);
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_all", null,
            null, null, JsonSerializer.Serialize(new { pairIds = new[] { source.Id, destination.Id } }), SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var invalidatedAtAdmission = false;
        try
        {
            _ = await new SyncAuthorityCrossPairMoveCoordinator(source, remotes[source.Id], options,
                    destination, remotes[destination.Id], options, authority, admission: admission)
                .ExecuteAsync(plan, "ready-authority-" + dimension);
        }
        catch (SyncAuthorityPlanningInvalidatedException)
        {
            invalidatedAtAdmission = true;
        }
        True(invalidatedAtAdmission);
        var superseded = await authority.LoadOperationAsync(ready.OperationId);
        Equal(SyncAuthorityOperationStatus.Superseded, superseded.Status);
        Equal(0, superseded.Attempts.Count);
        Equal(uploadsBefore, remotes.Values.Sum(item => item.UploadCount));
        Equal(downloadsBefore, remotes.Values.Sum(item => item.DownloadCount));
        Equal(archivesBefore, remotes.Values.Sum(item => item.ArchiveCount));
        Equal(deletesBefore, remotes.Values.Sum(item => item.DeleteCount));
        Equal(createsBefore, remotes.Values.Sum(item => item.DirectoryCreateCount));
        Equal(movesBefore, remotes.Values.Sum(item => item.MoveCount));
        Equal("authority-source", remotes[source.Id].ReadText("moved.txt"));
        Equal(dimension == "destination-content" ? "edited-destination-version" : "authority-source",
            File.ReadAllText(Path.Combine(destination.LocalRoot, "moved.txt")));

        var retainedPairSet = await authority.LoadPairSetAsync();
        foreach (var pairId in new[] { source.Id, destination.Id })
        {
            var retained = retainedPairSet.Pairs.Single(item => item.Id == pairId);
            await authority.RetainPairDefinitionAsync(retained with { Enabled = false }, select: false);
        }
        remotes[unrelated.Id].PutWithId("progress.txt", "ready-authority-unrelated-progress",
            "id:ready-authority-progress-" + dimension);
        var unrelatedResult = await host.RunPairsAsync([unrelated], "Unaffected authority progress");
        True(unrelatedResult.Items.All(item => item.FailureMessage is null));
        Equal("ready-authority-unrelated-progress",
            File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_ready_authority_transition",
            dimension,
            readyOperationPersisted = true,
            planningParticipants = ready.PlanningAuthorities.Select(item => new
            {
                item.ObservationSetId,
                item.PairId,
                item.LocalScanGenerationId,
                item.DropboxScanGenerationId,
                item.ConnectionId,
                item.ObservationContentFingerprint,
                item.SynchronizationPointSetFingerprint,
                item.RootAuthorityFingerprint
            }).ToArray(),
            invalidatedAuthorities,
            retainedConnectionInventory = dimension == "connection-account"
                ? retainedConnections.Select(item => new
                {
                    item.ConnectionId,
                    item.Provider,
                    item.AccountId,
                    item.RootNamespaceId,
                    item.IsActive
                }).ToArray()
                : null,
            inactiveConnectionRegistered = dimension == "connection-account" ? (bool?)true : null,
            inactiveConnectionEligibleForPlanning = dimension == "connection-account" ? (bool?)false : null,
            dropboxRootNamespaceBoundToActiveConnection = dimension == "connection-account" ? (bool?)true : null,
            supportedEntryPoint,
            supportedEntryPointFenced,
            readyOperationUnchangedByRejectedEntry = supportedEntryPoint is null ? (bool?)null : true,
            finalAdmission = "Superseded",
            attempts = 0,
            providerOrFilesystemEffects = 0,
            destinationVersionPreserved = true,
            unaffectedPairProgressed = true
        }));
    }
}

static async Task OrdinaryReadyOperationInvalidationMatrixContract(string root)
{
    var cases = new[]
    {
        (Name: "create-local", Kind: SyncAuthorityOperationKind.CreateLocal),
        (Name: "create-remote", Kind: SyncAuthorityOperationKind.CreateRemote),
        (Name: "delete-local", Kind: SyncAuthorityOperationKind.DeleteLocal),
        (Name: "delete-remote", Kind: SyncAuthorityOperationKind.DeleteRemote),
        (Name: "move-local", Kind: SyncAuthorityOperationKind.MoveLocal),
        (Name: "move-remote", Kind: SyncAuthorityOperationKind.MoveRemote)
    };
    foreach (var testCase in cases)
    {
        var scenarioRoot = Path.Combine(root, "ordinary-ready-invalidation-" + testCase.Name);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var localRoot = Path.Combine(scenarioRoot, "pair");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "anchor.txt", "anchor");
        var startsWithSubject = testCase.Kind is SyncAuthorityOperationKind.DeleteLocal or
            SyncAuthorityOperationKind.DeleteRemote or SyncAuthorityOperationKind.MoveLocal or
            SyncAuthorityOperationKind.MoveRemote;
        if (startsWithSubject) Write(localRoot, "subject.txt", "subject-v1");
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                "/OrdinaryReadyInvalidation/" + testCase.Name,
                "id:ordinary-ready-invalidation-root-" + testCase.Name), testCase.Name,
            connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = true,
            RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                "id:ordinary-ready-invalidation-recovery-" + testCase.Name,
                connection.RootNamespaceId)
        };
        pair = await BindFixtureControlAsync(pair, localLease,
            await pairStore.GetOrCreateInstallationIdAsync());
        await pairStore.UpsertAsync(pair, select: true);
        var remote = new FakeSyncRemoteStore();
        remote.PutWithId("anchor.txt", "anchor", "id:ordinary-ready-anchor-" + testCase.Name);
        if (startsWithSubject)
            remote.PutWithId("subject.txt", "subject-v1", "id:ordinary-ready-subject-" + testCase.Name);
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync([pair], "Ordinary invalidation baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync([pair], "Ordinary invalidation stable baseline")).Items
            .All(item => item.FailureMessage is null));

        switch (testCase.Kind)
        {
            case SyncAuthorityOperationKind.CreateLocal:
                remote.PutWithId("new.txt", "new-v1", "id:ordinary-ready-new-" + testCase.Name);
                break;
            case SyncAuthorityOperationKind.CreateRemote:
                Write(localRoot, "new.txt", "new-v1");
                break;
            case SyncAuthorityOperationKind.DeleteLocal:
                remote.Delete("subject.txt");
                break;
            case SyncAuthorityOperationKind.DeleteRemote:
                File.Delete(Path.Combine(localRoot, "subject.txt"));
                break;
            case SyncAuthorityOperationKind.MoveLocal:
                remote.Move("subject.txt", "moved.txt");
                break;
            case SyncAuthorityOperationKind.MoveRemote:
                File.Move(Path.Combine(localRoot, "subject.txt"), Path.Combine(localRoot, "moved.txt"));
                break;
        }

        var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                (await ReservedReadyOperationsAsync(authority, pair.Id, testCase.Kind)).Count == 0 ||
                Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
                return;
            pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
        };
        var stagedRun = host.RunPairsAsync([pair], "Stage ordinary Ready invalidation");
        True(await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        True(await host.StopActiveRunAsync());
        True((await stagedRun.WaitAsync(TimeSpan.FromSeconds(10))).Stopped);
        host.CoordinatedPlanningPhaseForTest = null;

        var staleQueueItem = (await ReservedReadyOperationsAsync(authority, pair.Id, testCase.Kind))
            .Single(item => item.Kind == testCase.Kind);
        var stale = await authority.LoadOperationAsync(staleQueueItem.OperationId);
        Equal(SyncAuthorityOperationStatus.Ready, stale.Status);
        Equal(0, stale.Attempts.Count);
        True(stale.PlanningAuthorities.Count == 1 &&
             await authority.IsPlanningAuthorityCurrentAsync(stale.PlanningAuthorities[0]));
        var effectsBeforeInvalidation = (remote.UploadCount, remote.DownloadCount, remote.ArchiveCount,
            remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount);

        await new SyncAuthorityObservationCoordinator(pair, remote, authority,
                await authority.LoadActiveRootBindingIdAsync(pair.Id, "local"),
                await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox"))
            .ObserveAsync("monitor");
        True(!await authority.IsPlanningAuthorityCurrentAsync(stale.PlanningAuthorities[0]));
        Equal(effectsBeforeInvalidation,
            (remote.UploadCount, remote.DownloadCount, remote.ArchiveCount,
                remote.DeleteCount, remote.MoveCount, remote.DirectoryCreateCount));

        var completedRun = await host.RunPairsAsync([pair], "Authority-current ordinary successor");
        True(completedRun.Items.Single().FailureMessage is null,
            JsonSerializer.Serialize(completedRun.Items.Single()));
        var superseded = await authority.LoadOperationAsync(stale.OperationId);
        Equal(SyncAuthorityOperationStatus.Superseded, superseded.Status);
        Equal(0, superseded.Attempts.Count);
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(testCase.Kind,
            SyncAuthorityOperationStatus.Completed));
        var successorOperationIds = (await authority.LoadActivityProjectionAsync(500))
            .Select(item => item.OperationId)
            .Where(item => item is not null && !string.Equals(item, stale.OperationId, StringComparison.Ordinal))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var successorCandidates = new List<SyncAuthorityOperationSnapshot>();
        foreach (var operationId in successorOperationIds)
            successorCandidates.Add(await authority.LoadOperationAsync(operationId));
        var successor = successorCandidates.Single(item =>
            item.Kind == testCase.Kind && item.Status == SyncAuthorityOperationStatus.Completed);
        True(successor.Attempts.Count > 0);
        True(successor.PlanningAuthorities.Count == 1);

        switch (testCase.Kind)
        {
            case SyncAuthorityOperationKind.CreateLocal:
            case SyncAuthorityOperationKind.CreateRemote:
                Equal("new-v1", File.ReadAllText(Path.Combine(localRoot, "new.txt")));
                Equal("new-v1", remote.ReadText("new.txt"));
                break;
            case SyncAuthorityOperationKind.DeleteLocal:
            case SyncAuthorityOperationKind.DeleteRemote:
                True(!File.Exists(Path.Combine(localRoot, "subject.txt")));
                True(!remote.Exists("subject.txt"));
                break;
            case SyncAuthorityOperationKind.MoveLocal:
            case SyncAuthorityOperationKind.MoveRemote:
                True(!File.Exists(Path.Combine(localRoot, "subject.txt")));
                True(!remote.Exists("subject.txt"));
                Equal("subject-v1", File.ReadAllText(Path.Combine(localRoot, "moved.txt")));
                Equal("subject-v1", remote.ReadText("moved.txt"));
                break;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "ordinary_ready_operation_invalidation",
            scenario = testCase.Name,
            operationKind = testCase.Kind.ToString(),
            staleOperation = new
            {
                stale.OperationId,
                status = superseded.Status.ToString(),
                attempts = superseded.Attempts.Count,
                members = superseded.Members.Select(item => new
                {
                    item.MemberId,
                    item.PairId,
                    item.ObjectId,
                    item.Role,
                    item.RootBindingId,
                    item.ExpectedPathKey
                }).OrderBy(item => item.MemberId, StringComparer.Ordinal).ToArray(),
                planningAuthority = stale.PlanningAuthorities.Select(item => new
                {
                    item.ObservationSetId,
                    item.PairId,
                    item.ConnectionId,
                    item.LocalScanGenerationId,
                    item.DropboxScanGenerationId,
                    item.ObservationContentFingerprint,
                    item.SynchronizationPointSetFingerprint,
                    item.RootAuthorityFingerprint
                }).ToArray()
            },
            successorOperation = new
            {
                successor.OperationId,
                status = successor.Status.ToString(),
                attempts = successor.Attempts.Count,
                members = successor.Members.Select(item => new
                {
                    item.MemberId,
                    item.PairId,
                    item.ObjectId,
                    item.Role,
                    item.RootBindingId,
                    item.ExpectedPathKey
                }).OrderBy(item => item.MemberId, StringComparer.Ordinal).ToArray()
            },
            staleEffects = 0,
            authorityCurrentSuccessorOnly = true,
            finalContentConverged = true,
            totalEffectDelta = new
            {
                uploads = remote.UploadCount - effectsBeforeInvalidation.UploadCount,
                downloads = remote.DownloadCount - effectsBeforeInvalidation.DownloadCount,
                archives = remote.ArchiveCount - effectsBeforeInvalidation.ArchiveCount,
                deletes = remote.DeleteCount - effectsBeforeInvalidation.DeleteCount,
                moves = remote.MoveCount - effectsBeforeInvalidation.MoveCount,
                directoryCreates = remote.DirectoryCreateCount - effectsBeforeInvalidation.DirectoryCreateCount
            }
        }));
    }
}

static async Task PlanningAuthorityDimensionsContract(string root)
{
    var dimensions = new[]
    {
        "content-fingerprint", "synchronization-point-set", "connection-account",
        "root-authority", "configuration-revision", "selection-definition"
    };
    foreach (var dimension in dimensions)
    {
        var scenarioRoot = Path.Combine(root, "planning-authority-" + dimension);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

        async Task<SyncPair> CreatePairAsync(string name)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            Write(localRoot, "anchor.txt", name + "-anchor");
            using var localLease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
                    localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                    "/PlanningAuthority/" + dimension + "/" + name,
                    "id:planning-authority-root-" + dimension + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = false
            };
            pair = await BindFixtureControlAsync(pair, localLease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId("anchor.txt", name + "-anchor",
                "id:planning-authority-anchor-" + dimension + "-" + name);
            remotes[pair.Id] = remote;
            return pair;
        }

        var source = await CreatePairAsync("source");
        var destination = await CreatePairAsync("destination");
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        True((await host.RunPairsAsync([source, destination], "Planning authority baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync([source, destination], "Planning authority stable baseline")).Items
            .All(item => item.FailureMessage is null));

        async Task<SyncAuthorityPairObservationSet> LoadSetAsync(SyncPair pair)
        {
            var localRoot = await authority.LoadActiveRootBindingIdAsync(pair.Id, "local");
            var dropboxRoot = await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
            return await authority.LoadPlanningObservationSetAsync(pair.Id, pair.PolicyRevision,
                localRoot, dropboxRoot,
                await authority.LoadLatestCompleteScanIdAsync(pair.Id, "local"),
                await authority.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox"));
        }

        var observationSetId = "planning-authority-set-" + dimension;
        var sourceAuthority = (await LoadSetAsync(source)).Bind(observationSetId);
        var destinationSet = await LoadSetAsync(destination);
        var destinationAuthority = destinationSet.Bind(observationSetId);
        True(await authority.IsPlanningAuthorityCurrentAsync(sourceAuthority));
        True(await authority.IsPlanningAuthorityCurrentAsync(destinationAuthority));

        SyncAuthorityPlanningAuthority authorityToCheck = destinationAuthority;
        switch (dimension)
        {
            case "content-fingerprint":
                authorityToCheck = destinationAuthority with
                {
                    ObservationContentFingerprint = new string(
                        destinationAuthority.ObservationContentFingerprint![0] == 'a' ? 'b' : 'a', 64)
                };
                break;
            case "synchronization-point-set":
            {
                var point = destinationSet.SynchronizationPoints.Single();
                await authority.AcceptSynchronizationPointAsync(
                    "planning-authority-mutated-point-" + dimension, destination.Id, point.ObjectId,
                    point.LocalObjectVersionId, point.DropboxObjectVersionId, point.LocalMembershipId,
                    point.DropboxMembershipId);
                break;
            }
            case "connection-account":
                await authority.RetainPairDefinitionAsync(destination with
                {
                    ConnectionId = "connection-planning-authority-mutated"
                }, select: false);
                break;
            case "root-authority":
            {
                var retained = await authority.LoadRootBindingAuthorityForTestAsync(
                    destinationAuthority.LocalRootBindingId);
                await authority.RegisterRootBindingAsync(destinationAuthority.LocalRootBindingId,
                    destination.Id, "local", retained.Generation, retained.IdentityJson,
                    retained.DisplayPath + "-relocated");
                break;
            }
            case "configuration-revision":
                await authority.RegisterPairAsync(destination.Id, destination.PolicyRevision + 1);
                break;
            case "selection-definition":
                await authority.RetainPairDefinitionAsync(destination with
                {
                    SelectionRules = [new SyncSelectionRule("Excluded", false, "id:excluded-folder")]
                }, select: false);
                break;
        }

        True(await authority.IsPlanningAuthorityCurrentAsync(sourceAuthority));
        True(!await authority.IsPlanningAuthorityCurrentAsync(authorityToCheck));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "planning_authority_dimension_invalidation",
            dimension,
            unaffectedParticipantRemainsCurrent = true,
            changedParticipantInvalidated = true
        }));
    }
}

static async Task FinalEffectAdmissionGuardComparisonContract(string root)
{
    var results = new Dictionary<string, (SyncAuthorityOperationStatus Status, int Uploads,
        bool DestinationPresent)>(StringComparer.Ordinal);
    foreach (var guardEnabled in new[] { true, false })
    {
        var name = guardEnabled ? "guard-enabled" : "guard-disabled-control";
        var scenarioRoot = Path.Combine(root, "final-effect-admission-" + name);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var localRoot = Path.Combine(scenarioRoot, "pair");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "anchor.txt", "anchor");
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                "/FinalAdmission/" + name, "id:final-admission-root-" + name), name,
            connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = false
        };
        pair = await BindFixtureControlAsync(pair, localLease,
            await pairStore.GetOrCreateInstallationIdAsync());
        await pairStore.UpsertAsync(pair, select: true);
        var remote = new FakeSyncRemoteStore();
        remote.PutWithId("anchor.txt", "anchor", "id:final-admission-anchor-" + name);
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync([pair], "Admission baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync([pair], "Admission stable baseline")).Items
            .All(item => item.FailureMessage is null));
        Write(localRoot, "new.txt", "planned-version");
        var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
                return;
            pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
        };
        var plannedRun = host.RunPairsAsync([pair], "Admission plan");
        True(await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        True(await host.StopActiveRunAsync());
        True((await plannedRun.WaitAsync(TimeSpan.FromSeconds(10))).Stopped);
        host.CoordinatedPlanningPhaseForTest = null;
        var operation = (await authority.ListRunnableOperationsForPairAsync(pair.Id, 100))
            .Single(item => item.Kind == SyncAuthorityOperationKind.CreateRemote);
        var snapshot = await authority.LoadOperationAsync(operation.OperationId);
        True(snapshot.PlanningAuthorities.Count == 1 &&
             await authority.IsPlanningAuthorityCurrentAsync(snapshot.PlanningAuthorities[0]));

        Write(localRoot, "peer.txt", "new-peer");
        remote.PutWithId("peer.txt", "new-peer", "id:final-admission-peer-" + name);
        await new SyncAuthorityObservationCoordinator(pair, remote, authority,
                await authority.LoadActiveRootBindingIdAsync(pair.Id, "local"),
                await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox"))
            .ObserveAsync("monitor");
        True(!await authority.IsPlanningAuthorityCurrentAsync(snapshot.PlanningAuthorities[0]));
        var uploadsBefore = remote.UploadCount;
        SyncAuthorityDatabase.DisableFinalPlanningAuthorityGuardForTest = !guardEnabled;
        try
        {
            try
            {
                var dispatch = await new SyncAuthorityCreateRemoteExecutor(pair, remote, authority)
                    .ExecuteAsync(operation.OperationId, "final-admission-" + name);
                results[name] = (dispatch.Status, remote.UploadCount - uploadsBefore, remote.Exists("new.txt"));
            }
            catch (SyncAuthorityPlanningInvalidatedException) when (guardEnabled)
            {
                var superseded = await authority.LoadOperationAsync(operation.OperationId);
                results[name] = (superseded.Status, remote.UploadCount - uploadsBefore,
                    remote.Exists("new.txt"));
            }
        }
        finally
        {
            SyncAuthorityDatabase.DisableFinalPlanningAuthorityGuardForTest = false;
        }
    }

    Equal(SyncAuthorityOperationStatus.Superseded, results["guard-enabled"].Status);
    Equal(0, results["guard-enabled"].Uploads);
    True(!results["guard-enabled"].DestinationPresent);
    Equal(SyncAuthorityOperationStatus.Completed, results["guard-disabled-control"].Status);
    Equal(1, results["guard-disabled-control"].Uploads);
    True(results["guard-disabled-control"].DestinationPresent);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        metric = "final_effect_admission_guard_comparison",
        sameScenarioAndTiming = true,
        guardEnabled = new { status = "superseded", uploads = 0, destinationPresent = false },
        guardDisabledControl = new { status = "completed", uploads = 1, destinationPresent = true }
    }));
}

static Task<IReadOnlyList<SyncAuthorityOperationSnapshot>> ReservedMovesAsync(
    SyncAuthorityDatabase authority, string pairId) =>
    ReservedReadyOperationsAsync(authority, pairId, SyncAuthorityOperationKind.CrossPairMove);

static async Task<IReadOnlyList<SyncAuthorityOperationSnapshot>> ReservedReadyOperationsAsync(
        SyncAuthorityDatabase authority, string pairId, SyncAuthorityOperationKind kind)
    {
        // A fresh group's Ready reservations are deliberately absent from the
        // recovery/runnable queue until the group is activated by dispatch.
        // Read the actual reserved boundary without activating it in the test.
        await using var read = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = authority.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await read.OpenAsync();
        await using var command = read.CreateCommand();
        command.CommandText = "SELECT operation_id FROM operation " +
            "WHERE pair_id=$pair AND kind=$kind AND status='ready' ORDER BY operation_id;";
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$kind", SyncAuthorityNames.OperationKind(kind));
        var ids = new List<string>();
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) ids.Add(rows.GetString(0));
        return await authority.LoadOperationsAsync(ids);
    }

static async Task CrossPairRestartBeforeDispatchContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "restart-before-dispatch-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

        async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content,
            string providerIdentity)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            Write(localRoot, relativePath, content);
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    "/Restart/" + direction + "/" + name,
                    "id:restart-root-" + direction + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = false
            };
            pair = await BindFixtureControlAsync(pair, lease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId(relativePath, content, providerIdentity);
            remotes[pair.Id] = remote;
            return pair;
        }

        var source = await CreatePairAsync("source", "moved.txt", "move-payload",
            "id:restart-stable-object-" + direction);
        var destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
            "id:restart-destination-anchor-" + direction);
        var unrelated = await CreatePairAsync("unrelated", "independent.txt", "old-independent",
            "id:restart-unrelated-" + direction);
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
            "syncshark.db"));
        var pairs = new[] { source, destination, unrelated };
        var sourceUploadsBefore = 0;
        var destinationUploadsBefore = 0;
        var sourceDownloadsBefore = 0;
        var destinationDownloadsBefore = 0;
        var sourceMovesBefore = 0;
        var secondRelocationObserved = 0;
        var persistedPlanningParticipants = 0;
        SyncAuthorityBatchResult interrupted;

        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                   (pair, _) => remotes[pair.Id]))
        {
            await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            True((await host.RunPairsAsync(pairs, "Restart baseline")).Items
                .All(item => item.FailureMessage is null));
            True((await host.RunPairsAsync(pairs, "Restart stable baseline")).Items
                .All(item => item.FailureMessage is null));

            sourceUploadsBefore = remotes[source.Id].UploadCount;
            destinationUploadsBefore = remotes[destination.Id].UploadCount;
            sourceDownloadsBefore = remotes[source.Id].DownloadCount;
            destinationDownloadsBefore = remotes[destination.Id].DownloadCount;
            sourceMovesBefore = remotes[source.Id].MoveCount;
            if (direction == "local-to-dropbox")
                File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
                    Path.Combine(destination.LocalRoot, "moved.txt"));
            else
            {
                var expected = (await remotes[source.Id].ListEntriesAsync())
                    .Single(item => item.RelativePath == "moved.txt");
                await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved.txt");
            }
            remotes[unrelated.Id].PutWithId("progress.txt", "independent-progress",
                "id:restart-unrelated-progress-" + direction);

            host.CoordinatedPlanningPhaseForTest = async phase =>
            {
                if (!string.Equals(phase, "after-cross-pair-reservations-before-ordinary-planning",
                        StringComparison.Ordinal) || Volatile.Read(ref secondRelocationObserved) != 0)
                    return;
                // A coordinated iteration can be invalidated before it reserves a
                // move. Interrupt the actual retained-before-dispatch boundary,
                // not merely the first visit to this generic planning hook.
                var moveQueue = await ReservedMovesAsync(authority, source.Id);
                if (!moveQueue.Any(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove &&
                                          item.Status == SyncAuthorityOperationStatus.Ready) ||
                    Interlocked.CompareExchange(ref secondRelocationObserved, 1, 0) != 0)
                    return;
                Equal(0, await authority.SupersedeStaleUnissuedCrossPairReservationsAsync());
                Equal(SyncAuthorityOperationStatus.Ready,
                    (await authority.LoadOperationAsync(moveQueue.Single().OperationId)).Status);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "restart_before_dispatch_correlation_state",
                    direction,
                    queue = await authority.ListRunnableOperationsForPairAsync(source.Id, 500),
                    baselines = await authority.LoadLatestSynchronizationPointsAsync(source.Id),
                    baselineVersions = await Task.WhenAll((await authority.LoadLatestSynchronizationPointsAsync(source.Id))
                        .SelectMany(point => new[] { point.LocalObjectVersionId, point.DropboxObjectVersionId })
                        .Where(versionId => versionId is not null)
                        .Select(versionId => authority.LoadObjectVersionAsync(versionId!))),
                    sourceLocal = await authority.LoadLatestCompleteObservationAsync(source.Id, "local"),
                    sourceRemote = await authority.LoadLatestCompleteObservationAsync(source.Id, "dropbox"),
                    destinationLocal = await authority.LoadLatestCompleteObservationAsync(destination.Id, "local"),
                    destinationRemote = await authority.LoadLatestCompleteObservationAsync(destination.Id, "dropbox")
                }));
                if (direction == "local-to-dropbox")
                    File.Move(Path.Combine(destination.LocalRoot, "moved.txt"),
                        Path.Combine(destination.LocalRoot, "moved-again.txt"));
                else
                    remotes[destination.Id].Move("moved.txt", "moved-again.txt");
                var localBinding = await authority.LoadActiveRootBindingIdAsync(destination.Id, "local");
                var dropboxBinding = await authority.LoadActiveRootBindingIdAsync(destination.Id, "dropbox");
                await new SyncAuthorityObservationCoordinator(destination, remotes[destination.Id], authority,
                    localBinding, dropboxBinding).ObserveAsync("monitor");
                True(await host.StopActiveRunAsync());
            };
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception =>
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "restart_before_dispatch_failure",
                    direction,
                    failure = exception.ToString()
                }));
            try
            {
                interrupted = await host.RunPairsAsync(pairs, "Restart before dispatch");
            }
            finally
            {
                host.CoordinatedPlanningPhaseForTest = null;
                SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
            }

            var retainedQueue = await ReservedMovesAsync(authority, source.Id);
            if (!retainedQueue.Any(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove &&
                                          item.Status == SyncAuthorityOperationStatus.Ready))
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "restart_before_dispatch_missing_move",
                    direction,
                    secondRelocationObserved,
                    interrupted,
                    retainedQueue
                }));
            var retained = retainedQueue
                .Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove &&
                                item.Status == SyncAuthorityOperationStatus.Ready);
            var retainedOperation = await authority.LoadOperationAsync(retained.OperationId);
            True(retainedOperation.PlanningAuthorities.Count >= 2);
            True((await authority.ListRecoveryOperationsForPairAsync(source.Id, 500))
                .All(item => item.OperationId != retained.OperationId));
            persistedPlanningParticipants = retainedOperation.PlanningAuthorities.Count;
        }

        True(interrupted.Stopped);
        Equal(1, secondRelocationObserved);
        Equal(sourceUploadsBefore, remotes[source.Id].UploadCount);
        Equal(destinationUploadsBefore, remotes[destination.Id].UploadCount);
        Equal(sourceDownloadsBefore, remotes[source.Id].DownloadCount);
        Equal(destinationDownloadsBefore, remotes[destination.Id].DownloadCount);
        True(remotes[source.Id].Exists("moved.txt") || direction == "dropbox-to-local");
        True(!remotes[destination.Id].Exists("moved.txt"));

        var batchFailureMessages = new List<string>();
        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                   new HttpClient(new NoNetworkHttpHandler()), pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                   new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                   (pair, _) => remotes[pair.Id]))
        {
            await restarted.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = exception =>
            {
                lock (batchFailureMessages) batchFailureMessages.Add(exception.ToString());
            };
            try
            {
                var result = await restarted.RunPairsAsync(pairs, "Restarted coordinated run");
                if (remotes[source.Id].MoveCount == sourceMovesBefore)
                {
                    await using var diagnostic = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = authority.DatabasePath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    await diagnostic.OpenAsync();
                    await using var query = diagnostic.CreateCommand();
                    query.CommandText = "SELECT operation_id,status FROM operation WHERE kind='cross_pair_move' ORDER BY operation_id;";
                    var retainedStates = new List<object>();
                    await using var rows = await query.ExecuteReaderAsync();
                    while (await rows.ReadAsync())
                        retainedStates.Add(new { id = rows.GetString(0), status = rows.GetString(1) });
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        metric = "coordinated_restart_missing_effect",
                        direction,
                        result,
                        retainedStates,
                        batchFailureMessages
                    }));
                }
                True(result.Items.All(item => item.FailureMessage is null));
                True(result.Items.All(item => item.Result is { Failed: 0, NeedsVerification: 0 }));
                var unchanged = await restarted.RunPairsAsync(pairs, "Restarted unchanged coordinated run");
                True(unchanged.Items.All(item => item.FailureMessage is null &&
                    item.Result is { Failed: 0, NeedsVerification: 0, Uploaded: 0, Downloaded: 0 }));
            }
            finally
            {
                SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
            }
        }

        Equal(0, remotes[source.Id].UploadCount + remotes[destination.Id].UploadCount -
                 sourceUploadsBefore - destinationUploadsBefore);
        Equal(0, remotes[source.Id].DownloadCount + remotes[destination.Id].DownloadCount -
                 sourceDownloadsBefore - destinationDownloadsBefore);
        Equal(1, remotes[source.Id].MoveCount - sourceMovesBefore);
        True(!File.Exists(Path.Combine(source.LocalRoot, "moved.txt")));
        True(!File.Exists(Path.Combine(destination.LocalRoot, "moved.txt")));
        Equal("move-payload", File.ReadAllText(Path.Combine(destination.LocalRoot, "moved-again.txt")));
        True(!remotes[source.Id].Exists("moved.txt"));
        True(!remotes[destination.Id].Exists("moved.txt"));
        Equal("move-payload", remotes[destination.Id].ReadText("moved-again.txt"));
        Equal("independent-progress", File.ReadAllText(Path.Combine(unrelated.LocalRoot, "progress.txt")));
        var crossPairStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>())
            crossPairStatusCounts[status.ToString()] = await authority.CountOperationsWithStatusForTestAsync(
                SyncAuthorityOperationKind.CrossPairMove, status);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "coordinated_restart_before_dispatch",
            direction,
            persistedPlanningParticipants,
            crossPairStatusCounts,
            batchFailureMessages,
            staleTransactionSuperseded = 1,
            latestRelocationCompleted = 1,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            unrelatedPairProgress = true
        }));
        Equal(1, crossPairStatusCounts[nameof(SyncAuthorityOperationStatus.Superseded)]);
        Equal(1, crossPairStatusCounts[nameof(SyncAuthorityOperationStatus.Completed)]);
    }
}

static async Task CrossPairPauseBeforeEffectContract(string root)
{
    foreach (var direction in new[] { "local-to-dropbox", "dropbox-to-local" })
    {
        var scenarioRoot = Path.Combine(root, "cross-pair-pause-before-effect-" + direction);
        Directory.CreateDirectory(scenarioRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var connection = Binding().Normalize();
        await connectionStore.SaveNewAsync(connection);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

        async Task<SyncPair> CreatePairAsync(string name, string relativePath, string content,
            string providerIdentity)
        {
            var localRoot = Path.Combine(scenarioRoot, name);
            Directory.CreateDirectory(localRoot);
            Write(localRoot, relativePath, content);
            using var localLease = PilotDirectoryLease.Open(localRoot);
            var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path,
                    localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                    "/PauseCrossPair/" + direction + "/" + name,
                    "id:pause-cross-pair-root-" + direction + "-" + name),
                name, connection.RootNamespaceId) with
            {
                ConnectionId = connection.ConnectionId,
                InitialReviewApproved = true,
                PropagateDeletions = false
            };
            pair = await BindFixtureControlAsync(pair, localLease, installationId);
            await pairStore.UpsertAsync(pair, select: name == "source");
            var remote = new FakeSyncRemoteStore();
            remote.PutWithId(relativePath, content, providerIdentity);
            remotes[pair.Id] = remote;
            return pair;
        }

        var source = await CreatePairAsync("source", "moved.txt", "pause-move-payload",
            "id:pause-cross-pair-object-" + direction);
        var destination = await CreatePairAsync("destination", "anchor.txt", "destination-anchor",
            "id:pause-cross-pair-anchor-" + direction);
        var pairs = new[] { source, destination };
        var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            new HttpClient(new NoNetworkHttpHandler()), pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
            (pair, _) => remotes[pair.Id]);
        await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
        True((await host.RunPairsAsync(pairs, "Cross-pair pause baseline")).Items
            .All(item => item.FailureMessage is null));
        True((await host.RunPairsAsync(pairs, "Cross-pair pause stable baseline")).Items
            .All(item => item.FailureMessage is null));

        if (direction == "local-to-dropbox")
            File.Move(Path.Combine(source.LocalRoot, "moved.txt"),
                Path.Combine(destination.LocalRoot, "moved.txt"));
        else
        {
            var expected = (await remotes[source.Id].ListEntriesAsync())
                .Single(item => item.RelativePath == "moved.txt");
            await remotes[source.Id].MoveToAsync(expected, remotes[destination.Id], "moved.txt");
        }
        var sourceUploadsBefore = remotes[source.Id].UploadCount;
        var destinationUploadsBefore = remotes[destination.Id].UploadCount;
        var sourceDownloadsBefore = remotes[source.Id].DownloadCount;
        var destinationDownloadsBefore = remotes[destination.Id].DownloadCount;
        var sourceMovesBefore = remotes[source.Id].MoveCount;
        var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedOnce = 0;
        host.CoordinatedPlanningPhaseForTest = async phase =>
        {
            if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
                return;
            pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
        };
        var run = host.RunPairsAsync(pairs, "Cross-pair pause before effect");
        True(await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var pausedOperation = (await ReservedMovesAsync(authority, source.Id))
            .Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove);
        var pausedSnapshot = await authority.LoadOperationAsync(pausedOperation.OperationId);
        var currentBeforeMutation = await Task.WhenAll(pausedSnapshot.PlanningAuthorities
            .Select(item => authority.IsPlanningAuthorityCurrentAsync(item)));
        True(pausedSnapshot.PlanningAuthorities.Count == 2 && currentBeforeMutation.All(item => item));

        if (direction == "local-to-dropbox")
            File.Move(Path.Combine(destination.LocalRoot, "moved.txt"),
                Path.Combine(destination.LocalRoot, "moved-after-pause.txt"));
        else
            remotes[destination.Id].Move("moved.txt", "moved-after-pause.txt");
        await new SyncAuthorityObservationCoordinator(destination, remotes[destination.Id], authority,
                await authority.LoadActiveRootBindingIdAsync(destination.Id, "local"),
                await authority.LoadActiveRootBindingIdAsync(destination.Id, "dropbox"))
            .ObserveAsync("monitor");
        var currentAfterMutation = await Task.WhenAll(pausedSnapshot.PlanningAuthorities
            .Select(item => authority.IsPlanningAuthorityCurrentAsync(item)));
        True(currentAfterMutation.Any(item => !item));
        Equal(sourceUploadsBefore, remotes[source.Id].UploadCount);
        Equal(destinationUploadsBefore, remotes[destination.Id].UploadCount);
        Equal(sourceDownloadsBefore, remotes[source.Id].DownloadCount);
        Equal(destinationDownloadsBefore, remotes[destination.Id].DownloadCount);
        Equal(false, await host.TogglePauseActiveRunAsync());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        host.CoordinatedPlanningPhaseForTest = null;

        True(result.Items.All(item => item.FailureMessage is null));
        Equal(0, remotes[source.Id].UploadCount + remotes[destination.Id].UploadCount -
                 sourceUploadsBefore - destinationUploadsBefore);
        Equal(0, remotes[source.Id].DownloadCount + remotes[destination.Id].DownloadCount -
                 sourceDownloadsBefore - destinationDownloadsBefore);
        Equal(direction == "local-to-dropbox" ? 1 : 0,
            remotes[source.Id].MoveCount - sourceMovesBefore);
        True(!File.Exists(Path.Combine(source.LocalRoot, "moved.txt")));
        True(!File.Exists(Path.Combine(destination.LocalRoot, "moved.txt")));
        Equal("pause-move-payload",
            File.ReadAllText(Path.Combine(destination.LocalRoot, "moved-after-pause.txt")));
        True(!remotes[source.Id].Exists("moved.txt"));
        True(!remotes[destination.Id].Exists("moved.txt"));
        Equal("pause-move-payload", remotes[destination.Id].ReadText("moved-after-pause.txt"));
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Superseded));
        Equal(1, await authority.CountOperationsWithStatusForTestAsync(
            SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "cross_pair_pause_before_effect",
            direction,
            effectWhilePaused = false,
            staleOperationSuperseded = 1,
            successorCompleted = 1,
            fallbackUploads = 0,
            fallbackDownloads = 0,
            finalPath = "moved-after-pause.txt"
        }));
    }
}

static async Task CoordinatedPauseBeforeEffectContract(string root)
{
    var scenarioRoot = Path.Combine(root, "pause-before-effect");
    Directory.CreateDirectory(scenarioRoot);
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
    var pairStore = new SyncPairStore(appLock.StateAuthority);
    var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
    var connection = Binding().Normalize();
    await connectionStore.SaveNewAsync(connection);
    var localRoot = Path.Combine(scenarioRoot, "pair");
    Directory.CreateDirectory(localRoot);
    Write(localRoot, "paused.txt", "version-1");
    using var lease = PilotDirectoryLease.Open(localRoot);
    var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/Pause/before-effect", "id:pause-before-effect-root"),
        "pause-before-effect", connection.RootNamespaceId) with
    {
        ConnectionId = connection.ConnectionId,
        InitialReviewApproved = true,
        PropagateDeletions = false,
        RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
            "id:pause-before-effect-recovery", connection.RootNamespaceId)
    };
    pair = await BindFixtureControlAsync(pair, lease, await pairStore.GetOrCreateInstallationIdAsync());
    await pairStore.UpsertAsync(pair, select: true);
    var remote = new FakeSyncRemoteStore();
    remote.PutWithId("paused.txt", "version-1", "id:pause-before-effect-file");
    var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
    using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
        new HttpClient(new NoNetworkHttpHandler()), pairStore,
        new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
        new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
    await host.SaveTransferSettingsAsync(new SyncTransferSettings(2, 0, 0));
    True((await host.RunPairsAsync([pair], "Pause baseline")).Items
        .All(item => item.FailureMessage is null));
    True((await host.RunPairsAsync([pair], "Pause stable baseline")).Items
        .All(item => item.FailureMessage is null));
    Write(localRoot, "paused.txt", "version-2");
    File.SetLastWriteTimeUtc(Path.Combine(localRoot, "paused.txt"), DateTime.UtcNow.AddMinutes(1));
    var pauseReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pausedOnce = 0;
    host.CoordinatedPlanningPhaseForTest = async phase =>
    {
        if (!string.Equals(phase, "after-ordinary-planning-before-dispatch", StringComparison.Ordinal) ||
            Interlocked.CompareExchange(ref pausedOnce, 1, 0) != 0)
            return;
        pauseReached.TrySetResult(await host.TogglePauseActiveRunAsync() == true);
    };
    var uploadsBefore = remote.UploadCount;
    var run = host.RunPairsAsync([pair], "Pause before effect");
    var paused = await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var pausedOperation = (await authority.ListRunnableOperationsForPairAsync(pair.Id, 100))
        .Single(item => item.Kind == SyncAuthorityOperationKind.UpdateRemote);
    var pausedSnapshot = await authority.LoadOperationAsync(pausedOperation.OperationId);
    True(pausedSnapshot.PlanningAuthorities.Count == 1 &&
         await authority.IsPlanningAuthorityCurrentAsync(pausedSnapshot.PlanningAuthorities[0]));
    var contentBeforeResume = remote.ReadText("paused.txt");
    var uploadsBeforeResume = remote.UploadCount - uploadsBefore;
    var completedBeforeResume = run.IsCompleted;
    Write(localRoot, "paused.txt", "version-3");
    File.SetLastWriteTimeUtc(Path.Combine(localRoot, "paused.txt"), DateTime.UtcNow.AddMinutes(2));
    var localBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "local");
    var dropboxBinding = await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox");
    await new SyncAuthorityObservationCoordinator(pair, remote, authority, localBinding, dropboxBinding)
        .ObserveAsync("monitor");
    True(!await authority.IsPlanningAuthorityCurrentAsync(pausedSnapshot.PlanningAuthorities[0]));
    Equal("version-1", remote.ReadText("paused.txt"));
    Equal(0, remote.UploadCount - uploadsBefore);
    var resumed = await host.TogglePauseActiveRunAsync();
    var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
    host.CoordinatedPlanningPhaseForTest = null;

    True(paused);
    Equal("version-1", contentBeforeResume);
    Equal(0, uploadsBeforeResume);
    True(!completedBeforeResume);
    Equal(false, resumed);
    True(result.Items.All(item => item.FailureMessage is null));
    Console.WriteLine("Pause-resume actual outcome: " + JsonSerializer.Serialize(result));
    Equal("version-3", remote.ReadText("paused.txt"));
    Equal(1, remote.UploadCount - uploadsBefore);
    Equal(1, await authority.CountOperationsWithStatusForTestAsync(
        SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Superseded));
    Equal(1, await authority.CountOperationsWithStatusForTestAsync(
        SyncAuthorityOperationKind.UpdateRemote, SyncAuthorityOperationStatus.Completed));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        metric = "coordinated_pause_before_effect",
        effectWhilePaused = false,
        resumedEffectCount = 1,
        staleOperationSuperseded = true,
        uploadedVersion = "version-3"
    }));
    // A deadline in the NEXT observation used to throw away the completed previous round.
    Write(localRoot, "paused.txt", "version-4");
    File.SetLastWriteTimeUtc(Path.Combine(localRoot, "paused.txt"), DateTime.UtcNow.AddMinutes(3));
    using var deadline = new CancellationTokenSource();
    var priorUploads = remote.UploadCount;
    host.CoordinatedPlanningPhaseForTest = phase =>
    {
        if (phase == "after-dispatch-before-next-observation-set" && remote.UploadCount > priorUploads)
            deadline.Cancel();
        return Task.CompletedTask;
    };
    SyncAuthorityBatchResult cancelled;
    try { cancelled = await host.RunPairsAsync([pair], "Cancel after completed file", cancellationToken: deadline.Token); }
    finally { host.CoordinatedPlanningPhaseForTest = null; }
    True(cancelled.Stopped && cancelled.Items.Single().Result is { Uploaded: 1, Stopped: true });
    Equal("version-4", remote.ReadText("paused.txt"));
    Equal(priorUploads + 1, remote.UploadCount);
    var retained = (await authority.LoadRunHistoryAsync(20, 0)).First(item =>
        item.ResultJson?.Contains("\"stopped\":true", StringComparison.OrdinalIgnoreCase) == true);
    using var retainedJson = JsonDocument.Parse(retained.ResultJson!);
    var files = retainedJson.RootElement.GetProperty("pairs")[0].GetProperty("files");
    True(files.EnumerateArray().Any(item => item.GetProperty("RelativePath").GetString() == "paused.txt" &&
        item.GetProperty("Kind").GetInt32() == (int)SyncDecisionKind.Uploaded));
    True(files.EnumerateArray().Any(item => item.GetProperty("Kind").GetInt32() == (int)SyncDecisionKind.Stopped));
    await host.RunPairsAsync([pair], "Verify completed file is not replayed");
    Equal(priorUploads + 1, remote.UploadCount);
    Console.WriteLine("PASS coordinated cancellation retains completed per-file results, stopped reason and no-replay authority through the actual host.");
}

static async Task PairFailureIsolationContract()
{
    try
    {
    await WithTempRoot(async root =>
    {
        async Task RunScenarioAsync(string name, bool sameAccount, bool removeBadRoot,
            bool failProviderObservation = false, bool failLocalObservation = false)
        {
            var scenarioRoot = Path.Combine(root, name);
            Directory.CreateDirectory(scenarioRoot);
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            var connection = Binding().Normalize();
            await connectionStore.SaveNewAsync(connection);
            var installationId = await pairStore.GetOrCreateInstallationIdAsync();
            var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

            async Task<SyncPair> CreatePairAsync(string pairName, string connectionId)
            {
                var localRoot = Path.Combine(scenarioRoot, pairName);
                Directory.CreateDirectory(localRoot);
                var initialFiles = pairName == "healthy"
                    ? new[] { "stable.txt", "delete.txt", "move-old.txt" }
                    : new[] { "stable.txt", "dependent-source.txt" };
                foreach (var path in initialFiles) Write(localRoot, path, "baseline-" + path);
                using var lease = PilotDirectoryLease.Open(localRoot);
                var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                        lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                        "/Isolation/" + pairName, "id:isolation-" + name + "-" + pairName),
                    pairName, connection.RootNamespaceId) with
                {
                    ConnectionId = connectionId,
                    InitialReviewApproved = true,
                    PropagateDeletions = true,
                    RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                        "id:isolation-recovery", connection.RootNamespaceId)
                };
                pair = await BindFixtureControlAsync(pair, lease, installationId);
                await pairStore.UpsertAsync(pair, select: pairName == "healthy");
                var remote = new FakeSyncRemoteStore();
                foreach (var path in initialFiles)
                    remote.PutWithId(path, "baseline-" + path,
                        $"id:{name}:{pairName}:{path}");
                remotes[pair.Id] = remote;
                return pair;
            }

            var healthy = await CreatePairAsync("healthy", connection.ConnectionId);
            var bad = await CreatePairAsync("bad", connection.ConnectionId);
            var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state", "syncshark.db"));
            IReadOnlyList<SyncPair> retained;
            string? interruptedCrossPairOperationId = null;
            PilotLocalEntry? dependentLocalIdentityBefore = null;
            (int Uploads, int Downloads, int Archives, int Deletes, int Moves, int DirectoryCreates)?
                effectsAfterInterruptedMove = null;
            using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                       new HttpClient(new NoNetworkHttpHandler()), pairStore,
                       new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                       new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                       (pair, _) => remotes[pair.Id]))
            {
                SyncAuthorityRuntimeHost.BatchItemFailureForTest = error =>
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        metric = "pair_isolation_failure_boundary",
                        scenario = name,
                        failureType = error.GetType().Name,
                        error.Message,
                        error.StackTrace
                    }));
                retained = (await host.LoadPairsAsync()).Pairs;
                var first = await host.RunPairsAsync(retained, "Pair isolation baseline");
                Equal(2, first.Items.Count);
                True(first.Items.All(item => item.Result is not null));
                var stableBaseline = await host.RunPairsAsync([healthy], "Pair isolation stable baseline");
                True(stableBaseline.Items.Single().FailureMessage is null);

                if (sameAccount)
                {
                    // Plan both an exact delete and an internal move while every participant is
                    // healthy, then interrupt after the delete has been recoverably retained.
                    // These operation-bound participants must remain runnable after a peer fails.
                    remotes[healthy.Id].Delete("delete.txt");
                    remotes[healthy.Id].Move("move-old.txt", "move-new.txt");
                    SyncAuthorityDeleteLocalExecutor.PhaseForTest = phase =>
                    {
                        if (phase == "local-retained-before-receipt")
                            throw new IOException("simulated restart after recoverable delete effect");
                    };
                    try
                    {
                        var interrupted = await host.RunPairsAsync([healthy],
                            "Retain independent membership operations");
                        True(interrupted.Items.Single().Result?.Decisions.Any(decision =>
                                decision.RelativePath == "delete.txt" &&
                                decision.Kind == SyncDecisionKind.ResponseUnknown &&
                                decision.Reason.Contains("simulated restart after recoverable delete effect",
                                    StringComparison.Ordinal)) == true,
                            "The retained deletion interruption did not surface: " +
                            JsonSerializer.Serialize(interrupted.Items.Select(item => new
                            {
                                item.FailureType,
                                item.FailureMessage,
                                decisions = item.Result?.Decisions.Select(decision => new
                                    { decision.RelativePath, kind = decision.Kind.ToString(), decision.Reason })
                            })));
                    }
                    finally
                    {
                        SyncAuthorityDeleteLocalExecutor.PhaseForTest = null;
                    }
                    True(!File.Exists(Path.Combine(healthy.LocalRoot, "delete.txt")));
                    True(File.Exists(Path.Combine(healthy.LocalRoot, "move-old.txt")));
                    var unknownDelete = (await authority.ListRecoveryOperationsForPairAsync(healthy.Id, 100))
                        .Single(item => item.Kind == SyncAuthorityOperationKind.DeleteLocal &&
                            item.Status == SyncAuthorityOperationStatus.EffectUnknown);
                    var unknownSnapshot = await authority.LoadOperationAsync(unknownDelete.OperationId);
                    var unknownAttempt = unknownSnapshot.Attempts.Last();
                    var unknownReceipt = await authority.LoadLatestEffectReceiptAsync(
                        unknownDelete.OperationId, unknownAttempt.AttemptId);
                    Equal("unknown", unknownReceipt?.Certainty);
                    Equal("{}", unknownReceipt?.SourceIdentityJson);
                    Equal("{}", unknownReceipt?.DestinationIdentityJson);
                    await authority.RetainExecutionFailureAsync(unknownDelete.OperationId,
                        "repeat-unknown-retention-check", new IOException("second safe diagnostic only"),
                        CancellationToken.None);
                    await using var receiptRead = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = authority.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                    }.ToString());
                    await receiptRead.OpenAsync();
                    await using var receiptCount = receiptRead.CreateCommand();
                    receiptCount.CommandText = "SELECT count(*) FROM effect_receipt WHERE attempt_id=$attempt;";
                    receiptCount.Parameters.AddWithValue("$attempt", unknownAttempt.AttemptId);
                    Equal(1L, Convert.ToInt64(await receiptCount.ExecuteScalarAsync()));
                }

                string? heldBadRoot = null;
                if (failProviderObservation)
                {
                    // Move one real NTFS identity into the healthy root, then make the source
                    // participant unavailable. This is the decisive negative witness: simply
                    // removing the old blanket fence would misclassify the move as a create.
                    File.Move(Path.Combine(bad.LocalRoot, "dependent-source.txt"),
                        Path.Combine(healthy.LocalRoot, "dependent-destination.txt"));
                    remotes[bad.Id].BeforeListEntriesForTest = _ =>
                        throw new IOException("simulated provider observation failure after root verification");
                }
                else if (failLocalObservation)
                {
                    // Move one real Dropbox identity into the healthy root, then make the
                    // source local root unavailable. This mirrors the opposite direction:
                    // the exact provider identity must be fenced instead of downloaded as a create.
                    var cloudIdentity = $"id:{name}:bad:dependent-source.txt";
                    remotes[bad.Id].Delete("dependent-source.txt");
                    remotes[healthy.Id].PutWithId("dependent-cloud-destination.txt",
                        "baseline-dependent-source.txt", cloudIdentity);
                    heldBadRoot = bad.LocalRoot + "-temporarily-unavailable";
                    Directory.Move(bad.LocalRoot, heldBadRoot);
                }
                else if (removeBadRoot)
                    Directory.Delete(bad.LocalRoot, recursive: true);
                else
                {
                    bad = bad with { ConnectionId = "connection-unavailable" };
                    await authority.RetainPairDefinitionAsync(bad, select: false);
                }

                remotes[healthy.Id].Put("stable.txt", "provider replacement");
                remotes[healthy.Id].PutWithId("independent-create.txt", "independent",
                    $"id:{name}:healthy:independent-create");
                retained = (await host.LoadPairsAsync()).Pairs;
                var isolatedBatch = await host.RunPairsAsync(retained, "Run-all pair isolation");
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "pair_isolated_batch",
                    scenario = name,
                    items = isolatedBatch.Items.Select(item => new
                    {
                        pair = item.Pair.Name,
                        item.FailureType,
                        item.FailureMessage,
                        decisions = item.Result?.Decisions.Select(decision => new
                            { path = decision.RelativePath, kind = decision.Kind.ToString(), decision.Reason })
                    })
                }));
                Equal(2, isolatedBatch.Items.Count);
                True(isolatedBatch.Items.Single(item => item.Pair.Id == healthy.Id).Result is not null);
                True(isolatedBatch.Items.Single(item => item.Pair.Id == bad.Id).FailureMessage is not null);
                if (failProviderObservation)
                    True(isolatedBatch.Items.Single(item => item.Pair.Id == bad.Id).FailureMessage!
                        .Contains("simulated provider observation failure", StringComparison.Ordinal),
                        "The exact provider failure must survive rather than become a generic observation-set error.");

                var single = await host.RunPairsAsync([healthy], "Single-pair isolation");
                var healthySingle = single.Items.Single();
                True(healthySingle.FailureMessage is null && healthySingle.Result is not null);
                var healthyDecisions = isolatedBatch.Items.Single(item => item.Pair.Id == healthy.Id)
                    .Result!.Decisions.Concat(healthySingle.Result!.Decisions).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "pair_failure_isolation",
                    scenario = name,
                    healthyDecisions = healthyDecisions.Select(item => new
                        { path = item.RelativePath, kind = item.Kind.ToString(), item.Reason })
                }));
                Equal("provider replacement", File.ReadAllText(Path.Combine(healthy.LocalRoot, "stable.txt")));
                Equal("independent", File.ReadAllText(Path.Combine(healthy.LocalRoot, "independent-create.txt")));
                True(healthyDecisions.All(item => item.RelativePath != "<cross-pair-membership>"));
                if (sameAccount)
                {
                    True(!File.Exists(Path.Combine(healthy.LocalRoot, "delete.txt")));
                    True(File.Exists(Path.Combine(healthy.LocalRoot, "move-new.txt")));
                    True(!File.Exists(Path.Combine(healthy.LocalRoot, "move-old.txt")));
                }

                if (failProviderObservation || failLocalObservation)
                {
                    var dependentPath = failProviderObservation
                        ? "dependent-destination.txt"
                        : "dependent-cloud-destination.txt";
                    var dependent = healthyDecisions.Where(item =>
                        item.RelativePath == dependentPath).ToArray();
                    True(dependent.Length >= 1 && dependent.All(item => item.Kind == SyncDecisionKind.Blocked));
                    True(dependent.All(item => item.Reason.Contains(
                        failProviderObservation ? "exact NTFS identity" : "exact Dropbox identity",
                        StringComparison.Ordinal)));
                    if (failProviderObservation)
                    {
                        True(remotes[bad.Id].Exists("dependent-source.txt"));
                        True(!remotes[healthy.Id].Exists(dependentPath));
                        remotes[bad.Id].BeforeListEntriesForTest = null;
                    }
                    else
                    {
                        True(!remotes[bad.Id].Exists("dependent-source.txt"));
                        True(remotes[healthy.Id].Exists(dependentPath));
                        True(heldBadRoot is not null &&
                             File.Exists(Path.Combine(heldBadRoot, "dependent-source.txt")));
                        Directory.Move(heldBadRoot!, bad.LocalRoot);
                    }

                    // Restore the failed participant but interrupt after the real cross-root
                    // membership effect. A fresh runtime must reconcile the exact retained operation.
                    var dependentSourcePath = failProviderObservation
                        ? Path.Combine(healthy.LocalRoot, "dependent-destination.txt")
                        : Path.Combine(bad.LocalRoot, "dependent-source.txt");
                    var dependentHolder = failProviderObservation ? healthy : bad;
                    using (var dependentLease = PilotDirectoryLease.Open(dependentHolder.LocalRoot,
                               new PilotDirectoryIdentity(dependentHolder.LocalVolumeSerialNumber,
                                   dependentHolder.LocalRootFileId)))
                        dependentLocalIdentityBefore = (await new PilotLocalScanner().ScanAsync(dependentLease))
                            .Entries.Values.Single(item => string.Equals(
                                PilotPath.ResolveContainedPath(dependentHolder.LocalRoot, item.RelativePath),
                                dependentSourcePath, StringComparison.OrdinalIgnoreCase));
                    SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = phase =>
                    {
                        var expectedPhase = failProviderObservation
                            ? "dropbox-cross-root-moved-before-receipt"
                            : "local-cross-root-moved-before-receipt";
                        if (phase == expectedPhase)
                        {
                            True(host.StopActiveRunAsync().GetAwaiter().GetResult());
                            throw new IOException("simulated runtime interruption after cross-pair membership move");
                        }
                    };
                    try
                    {
                        await host.RunPairsAsync(retained, "Interrupt dependent cross-pair move");
                    }
                    finally
                    {
                        SyncAuthorityCrossPairMoveCoordinator.PhaseForTest = null;
                    }
                    True(!remotes[bad.Id].Exists("dependent-source.txt"));
                    True(remotes[healthy.Id].Exists(dependentPath));
                    Equal(1, await authority.CountOperationsWithStatusForTestAsync(
                        SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.EffectIssuing));
                    var retainedMove = (await authority.ListRunnableOperationsForPairAsync(bad.Id, 500))
                        .Single(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove);
                    interruptedCrossPairOperationId = retainedMove.OperationId;
                    var interruptedOperation = await authority.LoadOperationAsync(retainedMove.OperationId);
                    Equal(1, interruptedOperation.Attempts.Count);
                    effectsAfterInterruptedMove = (remotes.Values.Sum(item => item.UploadCount),
                        remotes.Values.Sum(item => item.DownloadCount),
                        remotes.Values.Sum(item => item.ArchiveCount),
                        remotes.Values.Sum(item => item.DeleteCount),
                        remotes.Values.Sum(item => item.MoveCount),
                        remotes.Values.Sum(item => item.DirectoryCreateCount));
                }
                else
                {
                    var recovery = await host.RecoverPairsAsync(retained);
                    Equal(2, recovery.Items.Count);
                    True(recovery.Items.Single(item => item.Pair.Id == healthy.Id).Result is not null);
                    True(recovery.Items.Single(item => item.Pair.Id == bad.Id).FailureMessage is not null);
                }
            }

            if (failProviderObservation || failLocalObservation)
            {
                using var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                    new HttpClient(new NoNetworkHttpHandler()), pairStore,
                    new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                    new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
                    (pair, _) => remotes[pair.Id]);
                retained = (await restarted.LoadPairsAsync()).Pairs;
                var recovery = await restarted.RecoverPairsAsync(retained);
                Equal(2, recovery.Items.Count);
                True(recovery.Items.All(item => item.FailureMessage is null));
                var dependentPath = failProviderObservation
                    ? "dependent-destination.txt"
                    : "dependent-cloud-destination.txt";
                Equal("baseline-dependent-source.txt", remotes[healthy.Id].ReadText(dependentPath));
                Equal("baseline-dependent-source.txt",
                    File.ReadAllText(Path.Combine(healthy.LocalRoot, dependentPath)));
                True(interruptedCrossPairOperationId is not null && dependentLocalIdentityBefore is not null &&
                     effectsAfterInterruptedMove is not null);
                var completedOperation = await authority.LoadOperationAsync(interruptedCrossPairOperationId!);
                Equal(SyncAuthorityOperationStatus.Completed, completedOperation.Status);
                Equal(1, completedOperation.Attempts.Count);
                Equal(1, await authority.CountOperationsWithStatusForTestAsync(
                    SyncAuthorityOperationKind.CrossPairMove, SyncAuthorityOperationStatus.Completed));
                foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>().Where(item =>
                             item != SyncAuthorityOperationStatus.Completed))
                    Equal(0, await authority.CountOperationsWithStatusForTestAsync(
                        SyncAuthorityOperationKind.CrossPairMove, status));
                var expectedEffects = effectsAfterInterruptedMove ??
                                      throw new InvalidDataException("The interrupted effect counters are absent.");
                Equal(expectedEffects.Uploads, remotes.Values.Sum(item => item.UploadCount));
                Equal(expectedEffects.Downloads, remotes.Values.Sum(item => item.DownloadCount));
                Equal(expectedEffects.Archives, remotes.Values.Sum(item => item.ArchiveCount));
                Equal(expectedEffects.Deletes, remotes.Values.Sum(item => item.DeleteCount));
                Equal(expectedEffects.Moves, remotes.Values.Sum(item => item.MoveCount));
                Equal(expectedEffects.DirectoryCreates,
                    remotes.Values.Sum(item => item.DirectoryCreateCount));
                using (var healthyLease = PilotDirectoryLease.Open(healthy.LocalRoot,
                           new PilotDirectoryIdentity(healthy.LocalVolumeSerialNumber,
                               healthy.LocalRootFileId)))
                {
                    var recoveredLocal = (await new PilotLocalScanner().ScanAsync(healthyLease)).Entries
                        [dependentPath];
                    Equal(dependentLocalIdentityBefore!.VolumeSerialNumber, recoveredLocal.VolumeSerialNumber);
                    Equal(dependentLocalIdentityBefore.FileId, recoveredLocal.FileId);
                }
                var recoveredRemote = (await remotes[healthy.Id].ListEntriesAsync())
                    .Single(item => item.RelativePath == dependentPath);
                Equal($"id:{name}:bad:dependent-source.txt", recoveredRemote.Id);
                Equal(0, (await authority.LoadRecoveryArtifactsAsync())
                    .Count(item => item.OperationId == interruptedCrossPairOperationId));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    metric = "dependent_cross_pair_startup_recovery",
                    scenario = name,
                    direction = failProviderObservation ? "local-to-dropbox" : "dropbox-to-local",
                    exactObjectBlockedBeforeParticipantRecovery = true,
                    identityMovedWithoutDeleteResync = true,
                    startupRecoveryCompleted = true,
                    interruptedStatus = "EffectIssuing",
                    completedStatus = "Completed",
                    retainedAttempts = 1,
                    additionalEffectsDuringRecovery = 0,
                    localIdentityPreserved = true,
                    providerIdentityPreserved = true,
                    unexpectedRecoveryArtifacts = 0,
                    abruptRecoveryEquivalenceKey = CrossPairAbruptRecoveryEquivalenceKey(
                        failProviderObservation ? "local-to-dropbox" : "dropbox-to-local"),
                    equivalentOperationKind = "CrossPairMove",
                    equivalentChildExitCode = 215
                }));
            }
        }

        await RunScenarioAsync("inaccessible-root", sameAccount: true, removeBadRoot: true);
        await RunScenarioAsync("unavailable-account", sameAccount: false, removeBadRoot: false);
        await RunScenarioAsync("provider-observation", sameAccount: true, removeBadRoot: false,
            failProviderObservation: true);
        await RunScenarioAsync("local-observation", sameAccount: true, removeBadRoot: false,
            failLocalObservation: true);
        await SelectiveExclusionFailureIsolationContract(root);
    });
    }
    finally
    {
        SyncAuthorityRuntimeHost.BatchItemFailureForTest = null;
    }
}

static async Task SelectiveExclusionFailureIsolationContract(string root)
{
    var scenarioRoot = Path.Combine(root, "selective-exclusion-peer-failure");
    Directory.CreateDirectory(scenarioRoot);
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(scenarioRoot, "state", "instance.lock"));
    var pairStore = new SyncPairStore(appLock.StateAuthority);
    var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
    var connection = Binding().Normalize();
    await connectionStore.SaveNewAsync(connection);
    var installationId = await pairStore.GetOrCreateInstallationIdAsync();
    var remotes = new Dictionary<string, FakeSyncRemoteStore>(StringComparer.Ordinal);

    async Task<SyncPair> CreatePairAsync(string name)
    {
        var localRoot = Path.Combine(scenarioRoot, name);
        Directory.CreateDirectory(Path.Combine(localRoot, "select"));
        if (name == "healthy")
        {
            Write(localRoot, "select/anchor.txt", "healthy-anchor");
            Write(localRoot, "stable.txt", "stable-before");
        }
        else
            Write(localRoot, "select/dependent.txt", "dependent-payload");
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                "/SelectionFailure/" + name, "id:selection-failure-root-" + name),
            name, connection.RootNamespaceId) with
        {
            ConnectionId = connection.ConnectionId,
            InitialReviewApproved = true,
            PropagateDeletions = true,
            RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                "id:selection-failure-recovery", connection.RootNamespaceId)
        };
        pair = await BindFixtureControlAsync(pair, lease, installationId);
        await pairStore.UpsertAsync(pair, select: name == "healthy");
        var remote = new FakeSyncRemoteStore();
        remote.PutDirectoryWithId("select", "id:selection-failure-folder-" + name);
        if (name == "healthy")
        {
            remote.PutWithId("select/anchor.txt", "healthy-anchor",
                "id:selection-failure-anchor");
            remote.PutWithId("stable.txt", "stable-before",
                "id:selection-failure-stable");
        }
        else
            remote.PutWithId("select/dependent.txt", "dependent-payload",
                "id:selection-failure-dependent");
        remotes[pair.Id] = remote;
        return pair;
    }

    var healthy = await CreatePairAsync("healthy");
    var failed = await CreatePairAsync("failed");
    var authority = new SyncAuthorityDatabase(Path.Combine(scenarioRoot, "vNext", "state",
        "syncshark.db"));
    using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
        new HttpClient(new NoNetworkHttpHandler()), pairStore,
        new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
        new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority,
        (pair, _) => remotes[pair.Id]);
    var pairs = new[] { healthy, failed };
    True((await host.RunPairsAsync(pairs, "Selection failure baseline")).Items
        .All(item => item.FailureMessage is null));
    True((await host.RunPairsAsync(pairs, "Selection failure stable baseline")).Items
        .All(item => item.FailureMessage is null));

    File.Move(Path.Combine(failed.LocalRoot, "select", "dependent.txt"),
        Path.Combine(healthy.LocalRoot, "select", "dependent.txt"));
    remotes[failed.Id].BeforeListEntriesForTest = _ =>
        throw new IOException("simulated provider observation failure during exclusion change");
    var exclusionRejected = false;
    try
    {
        _ = await host.SetFolderSelectionAsync(healthy.Id, "select", included: false);
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains(
               "local changes", StringComparison.OrdinalIgnoreCase))
    {
        exclusionRejected = true;
    }
    True(exclusionRejected);
    var retainedHealthy = (await authority.LoadPairSetAsync()).Pairs.Single(item => item.Id == healthy.Id);
    True(SyncSelectionPolicy.IsIncluded("select/dependent.txt", retainedHealthy.SelectionRules));

    remotes[healthy.Id].Put("stable.txt", "stable-after");
    remotes[healthy.Id].PutWithId("outside.txt", "outside-progress",
        "id:selection-failure-outside");
    var archivesBefore = remotes.Values.Sum(item => item.ArchiveCount);
    var deletesBefore = remotes.Values.Sum(item => item.DeleteCount);
    var movesBefore = remotes.Values.Sum(item => item.MoveCount);
    var result = await host.RunPairsAsync((await host.LoadPairsAsync()).Pairs,
        "Selection change with failed peer");
    True(result.Items.Single(item => item.Pair.Id == healthy.Id).FailureMessage is null);
    True(result.Items.Single(item => item.Pair.Id == failed.Id).FailureMessage is not null);
    var dependent = result.Items.Single(item => item.Pair.Id == healthy.Id).Result!.Decisions
        .Where(item => item.RelativePath == "select/dependent.txt").ToArray();
    True(dependent.Length > 0 && dependent.All(item => item.Kind == SyncDecisionKind.Blocked));
    True(dependent.All(item => item.Reason.Contains("exact NTFS identity", StringComparison.Ordinal)));
    Equal("stable-after", File.ReadAllText(Path.Combine(healthy.LocalRoot, "stable.txt")));
    Equal("outside-progress", File.ReadAllText(Path.Combine(healthy.LocalRoot, "outside.txt")));
    Equal("dependent-payload", File.ReadAllText(Path.Combine(healthy.LocalRoot, "select", "dependent.txt")));
    Equal("dependent-payload", remotes[failed.Id].ReadText("select/dependent.txt"));
    True(!remotes[healthy.Id].Exists("select/dependent.txt"));
    Equal(archivesBefore, remotes.Values.Sum(item => item.ArchiveCount));
    Equal(deletesBefore, remotes.Values.Sum(item => item.DeleteCount));
    Equal(movesBefore, remotes.Values.Sum(item => item.MoveCount));
    remotes[failed.Id].BeforeListEntriesForTest = null;

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        metric = "selective_exclusion_failure_isolation",
        exclusionRejectedBeforeAuthorityLoss = true,
        retainedSelectionIncludesUncertainSubtree = true,
        failedPeerIdentityFenced = true,
        uncertainPathBlocked = true,
        destructiveEffects = 0,
        independentHealthyChangesCompleted = 2
    }));
}

static void CorrectionWitness(string correctionId, string witness)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        correction_id = correctionId,
        status = "executed_pass",
        witness
    }));
}

static Task ReleaseScaleMetricsContract()
{
    return WithTempRoot(async root =>
    {
        const int fileCount = 1_500;
        var localRoot = Path.Combine(root, "inventory");
        Directory.CreateDirectory(localRoot);
        for (var index = 0; index < fileCount; index++)
        {
            var folder = Path.Combine(localRoot, "group-" + (index / 100).ToString("D2"));
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "item-" + index.ToString("D5") + ".txt"),
                index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        PilotLocalScan scan;
        var inventory = Stopwatch.StartNew();
        using (var lease = PilotDirectoryLease.Open(localRoot))
            scan = await new PilotLocalScanner().ScanAsync(lease);
        inventory.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var memoryDelta = Math.Max(0, memoryAfter - memoryBefore);
        Equal(fileCount + 15, scan.Entries.Count);
        Equal(fileCount, scan.FilesHashed);
        True(inventory.Elapsed < TimeSpan.FromSeconds(30));
        True(memoryDelta < 256L * 1024 * 1024);

        var syncRoot = Path.Combine(root, "provider-free-sync");
        Directory.CreateDirectory(syncRoot);
        Write(syncRoot, "nested/local.txt", "local payload");
        using var syncLock = PilotInstanceLock.Acquire(Path.Combine(root, "provider-free-state", "instance.lock"));
        using var syncLease = PilotDirectoryLease.Open(syncRoot);
        var syncConnection = Binding().Normalize();
        var syncPairStore = new SyncPairStore(syncLock.StateAuthority);
        var syncPair = SyncPair.FromPilot(new PilotSettings(syncLease.Path,
                syncLease.Identity.VolumeSerialNumber, syncLease.Identity.FileId,
                "/Scale Contract", "id:scale-contract"), "Scale contract",
            syncConnection.RootNamespaceId) with
        {
            ConnectionId = syncConnection.ConnectionId,
            InitialReviewApproved = true
        };
        syncPair = await BindFixtureControlAsync(syncPair, syncLease,
            await syncPairStore.GetOrCreateInstallationIdAsync());
        await syncPairStore.UpsertAsync(syncPair);
        var syncConnectionStore = new DropboxConnectionStore(syncLock.StateAuthority);
        await syncConnectionStore.SaveNewAsync(syncConnection);
        var syncRemote = new FakeSyncRemoteStore();
        syncRemote.PutDirectory("nested");
        syncRemote.Put("nested/dropbox.txt", "dropbox payload");
        using var noNetworkHttp = new HttpClient(new NoNetworkHttpHandler());
        using var syncHost = new SyncAuthorityRuntimeHost(syncLock.StateAuthority, noNetworkHttp,
            syncPairStore, new SyncRuntimeStateStore(syncLock.StateAuthority), syncConnectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"),
            new SyncAuthorityDatabase(Path.Combine(root, "provider-free-authority", "syncshark.db")),
            (_, _) => syncRemote);
        var consistencyTimer = Stopwatch.StartNew();
        var syncBatch = await syncHost.RunPairsAsync([syncPair], "Scale consistency contract");
        consistencyTimer.Stop();
        var consistency = consistencyTimer.Elapsed;
        var syncItem = syncBatch.Items.Single();
        True(syncItem.FailureMessage is null && syncItem.Result is not null &&
             syncItem.Result.Blocked == 0 && syncItem.Result.Conflicts == 0);
        Equal("local payload", syncRemote.ReadText("nested/local.txt"));
        Equal("dropbox payload", await File.ReadAllTextAsync(
            Path.Combine(syncRoot, "nested", "dropbox.txt")));
        True(consistency < TimeSpan.FromSeconds(10));

        const int throughputBytes = 8 * 1024 * 1024;
        var bytes = new byte[throughputBytes];
        RandomNumberGenerator.Fill(bytes);
        var throughputRemote = new FakeSyncRemoteStore();
        await using var stream = new MemoryStream(bytes, writable: false);
        var hash = await DropboxContentHash.ComputeAsync(stream);
        stream.Position = 0;
        var transfer = Stopwatch.StartNew();
        await throughputRemote.UploadCreateOnlyAsync("throughput.bin", stream, hash);
        transfer.Stop();
        var mibPerSecond = throughputBytes / 1024d / 1024d / Math.Max(0.001, transfer.Elapsed.TotalSeconds);
        True(mibPerSecond >= 2);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "release_scale_targets",
            inventoryEntries = scan.Entries.Count,
            inventoryTargetMilliseconds = 30_000,
            inventoryMilliseconds = Math.Round(inventory.Elapsed.TotalMilliseconds),
            retainedMemoryTargetMiB = 256,
            retainedMemoryMiB = Math.Round(memoryDelta / 1024d / 1024d, 2),
            providerFreeConsistencyTargetMilliseconds = 10_000,
            providerFreeConsistencyMilliseconds = Math.Round(consistency.TotalMilliseconds),
            providerFreeThroughputTargetMiBPerSecond = 2,
            providerFreeThroughputMiBPerSecond = Math.Round(mibPerSecond, 2)
        }));
    });
}

static Task DropboxConnectionLifecycle()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        var retained = Binding().Normalize() with
        {
            DisplayName = "Fixture Account",
            Email = "fixture@example.invalid",
            AccountType = "basic",
            ConnectedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LastVerifiedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await connectionStore.SaveNewAsync(retained);
        var inertCapabilities = new ProviderCapabilityFlags(true, false, true, false, false, false,
            true, true, false, true, true, false);
        await connectionStore.SaveInertConnectionAsync(new ProviderConnectionRecord(
            "connection-inert-google-drive-fixture", "google-drive", "inert-account",
            "inert-root", "inert-home", "Future account", "", "", "", false,
            inertCapabilities));
        var credentialStore = new MemoryDropboxRefreshTokenStore("retained-refresh");
        var handler = new DropboxConnectionLifecycleHandler("account-id", "root-namespace",
            "home-namespace") { ThrowOnRevoke = true };
        using var httpClient = new HttpClient(handler);
        var pairRoot = Path.Combine(root, "pair");
        Directory.CreateDirectory(pairRoot);
        await File.WriteAllTextAsync(Path.Combine(pairRoot, "preserved.txt"), "preserved");
        PilotDirectoryIdentity pairIdentity;
        using (var lease = PilotDirectoryLease.Open(pairRoot)) pairIdentity = lease.Identity;
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var pair = new SyncPair("connection-lifecycle-pair", "Connection lifecycle", pairRoot,
            pairIdentity.VolumeSerialNumber, pairIdentity.FileId, "/ConnectionLifecycle",
            "id:connection-lifecycle", ConnectionId: retained.ConnectionId);
        await pairStore.UpsertAsync(pair);
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   authority))
        {
            var priorVerification = retained.LastVerifiedUtc!.Value;
            var verified = await host.GetDropboxConnectionStatusAsync();
            var persistedVerification = connectionStore.Load()!.LastVerifiedUtc;
            True(verified.LiveVerified && verified.Binding!.LastVerifiedUtc > priorVerification);
            True(persistedVerification > priorVerification);
            Equal(verified.Binding!.LastVerifiedUtc, persistedVerification);

            await host.DisconnectDropboxAsync();
            var disconnected = await host.GetDropboxConnectionStatusAsync(verifyLive: false);
            True(await credentialStore.ReadAsync() is null);
            Equal(retained.ConnectionId, connectionStore.Load()!.ConnectionId);
            Equal(retained.AccountId, disconnected.Binding!.AccountId);
            True(!disconnected.HasProtectedCredential && !disconnected.LiveVerified);
            True(pairStore.Load().Pairs.Any(item => item.Id == pair.Id));
            Equal("preserved", await File.ReadAllTextAsync(Path.Combine(pairRoot, "preserved.txt")));
        }

        var receiver = new FixtureAuthorizationReceiver();
        var coordinator = new DropboxConnectionCoordinator(httpClient, receiver, credentialStore,
            connectionStore);
        Uri? authorizationUri = null;
        var reconnected = await coordinator.ConnectAsync(uri =>
        {
            authorizationUri = uri;
            return Task.CompletedTask;
        });
        Equal(retained.ConnectionId, reconnected.ConnectionId);
        Equal("refresh-account-id", await credentialStore.ReadAsync());
        Equal(2, connectionStore.LoadConnections().Count);
        True(connectionStore.LoadConnections().Any(item =>
            item.ConnectionId == "connection-inert-google-drive-fixture" && !item.IsActive));
        True(authorizationUri is not null &&
             authorizationUri.Query.Contains("code_challenge=", StringComparison.Ordinal) &&
             authorizationUri.Query.Contains("state=", StringComparison.Ordinal));

        handler.ExpiresInSeconds = 0;
        var refreshing = new RefreshingDropboxAccessTokenSource(httpClient, credentialStore, reconnected);
        _ = await refreshing.GetAccessTokenAsync();
        _ = await refreshing.GetAccessTokenAsync();
        True(handler.RefreshRequests >= 2);

        handler.CurrentAccountStatus = HttpStatusCode.Unauthorized;
        using (var revokedHost = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   authority))
        {
            var revoked = await revokedHost.GetDropboxConnectionStatusAsync();
            True(revoked.HasProtectedCredential && !revoked.LiveVerified && revoked.FailureType is not null);
        }
        handler.CurrentAccountStatus = HttpStatusCode.OK;

        handler.OversizedCurrentAccountResponse = true;
        await ThrowsAsync<InvalidDataException>(() => DropboxOAuth.GetCurrentAccountAsync(
            httpClient, "fixture-access-token", default));
        handler.OversizedCurrentAccountResponse = false;

        await credentialStore.DeleteAsync();
        handler.AccountId = "different-account";
        handler.RootNamespaceId = "different-root";
        handler.HomeNamespaceId = "different-home";
        await ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(_ => Task.CompletedTask));
        Equal(retained.ConnectionId, connectionStore.Load()!.ConnectionId);
        True(await credentialStore.ReadAsync() is null);

        var records = connectionStore.LoadConnections();
        Equal(2, records.Count);
        Equal(2, records.Select(item => item.ConnectionId).Distinct(StringComparer.Ordinal).Count());
        Equal(1, records.Count(item => item.IsActive));
    });
}

static Task DropboxRewindRollbackContract()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        Write(localRoot, "stable.txt", "checkpoint-stable");
        Write(localRoot, "concurrent.txt", "checkpoint-concurrent");

        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var binding = Binding().Normalize();
        SyncPair pair;
        using (var localLease = PilotDirectoryLease.Open(localRoot))
        {
            pair = new SyncPair("0123456789abcdefabcd", "Rewind fixture", localLease.Path,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                "/RewindFixture", "id:rewind-fixture", PropagateDeletions: true,
                ConnectionId: binding.ConnectionId, InitialReviewApproved: true,
                RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:rewind-recovery", "root-namespace"));
            pair = await BindFixtureControlAsync(pair, localLease, installationId);
        }
        await pairStore.UpsertAsync(pair);

        var remote = new FakeSyncRemoteStore();
        remote.Put("stable.txt", "checkpoint-stable");
        remote.Put("concurrent.txt", "checkpoint-concurrent");
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(binding);
        var credentialStore = new MemoryDropboxRefreshTokenStore("rewind-refresh-token");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var httpClient = new HttpClient();

        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   authority, (_, _) => remote))
        {
            await host.LoadPairsAsync();
            await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision,
                new SyncAuthorityPairConfiguration("recoverable", 0, 1,
                    "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                    AutomaticPruning: false, RecoveryRetentionDays: 30));
            await authority.RetainPairDefinitionAsync(pair, select: true);
            var baseline = await host.RunPairsAsync([pair], "Fixture baseline");
            True(baseline.Items.Single().Result is { Conflicts: 0, Blocked: 0 },
                "Rewind baseline did not converge: " + JsonSerializer.Serialize(baseline.Items.Single()));

            var handoff = await host.PrepareRewindAsync(pair.Id);
            Equal(SyncRewindStatus.AwaitingDropbox, handoff.Session.Status);
            True(handoff.DropboxFolderUrl.AbsoluteUri.Contains("RewindFixture", StringComparison.Ordinal));

            remote.Put("stable.txt", "rewound-stable");
            remote.Put("concurrent.txt", "rewound-concurrent");
            remote.Put("restored.txt", "rewind-restored");
            Write(localRoot, "concurrent.txt", "post-checkpoint-local-work");

            await ThrowsAsync<InvalidOperationException>(() => host.ResumeAfterRewindAsync(pair.Id));

            var preview = await host.PreviewRewindImportAsync(pair.Id);
            Equal(SyncRewindStatus.PreviewReady, preview.Status);
            var previewRows = preview.Preview ?? throw new InvalidOperationException("Rewind preview was absent.");
            True(previewRows.Any(item => item.RelativePath == "stable.txt" &&
                                              item.Group == "Rewind changed Dropbox" && !item.LocalChanged));
            True(previewRows.Any(item => item.RelativePath == "concurrent.txt" &&
                                             item.Group == "Local work also changed" && item.LocalChanged));
            True(previewRows.Any(item => item.RelativePath == "restored.txt" &&
                                             item.Group == "Rewind restored in Dropbox"));

            var uploadCount = remote.UploadCount;
            var ordinary = await host.RunPairsAsync([pair], "Automatic monitor");
            Equal(1, ordinary.Items.Single().Result!.Blocked);
            Equal(uploadCount, remote.UploadCount);
            Equal("rewound-stable", remote.ReadText("stable.txt"));

            var rewindStore = new SyncRewindStore(authority);
            foreach (var unsafeStatus in new[]
                     {
                         SyncRewindStatus.Applying, SyncRewindStatus.PartiallyApplied,
                         SyncRewindStatus.EffectUnknown, SyncRewindStatus.ApplyBlocked
                     })
            {
                await rewindStore.UpsertAsync(preview with
                {
                    Status = unsafeStatus,
                    Detail = "durable restart status fence"
                });
                using var statusRestart = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient,
                    pairStore, new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
                    credentialStore, authority, (_, _) => remote);
                Equal(unsafeStatus, (await statusRestart.LoadRewindSessionsAsync()).Single(item =>
                    item.PairId == pair.Id && item.Status != SyncRewindStatus.Resumed).Status);
                await ThrowsAsync<InvalidOperationException>(() => statusRestart.ResumeAfterRewindAsync(pair.Id));
            }
            await rewindStore.UpsertAsync(preview);

            var confirmed = SyncRewindApplyConfirmation.FromSession(preview);
            var downloadsBeforeConsentChecks = remote.DownloadCount;
            foreach (var changed in new[]
                     {
                         confirmed with { SessionId = "a-different-reviewed-session" },
                         confirmed with { LocalPreviewScanId = "a-different-local-preview" },
                         confirmed with { DropboxPreviewScanId = "a-different-Dropbox-preview" }
                     })
                await ThrowsAsync<InvalidOperationException>(() => host.ApplyRewindImportAsync(pair.Id, changed));
            Equal(downloadsBeforeConsentChecks, remote.DownloadCount);
            Equal(SyncRewindStatus.PreviewReady, (await rewindStore.LoadActiveAsync(pair.Id))!.Status);

            // A genuine newly observed preview replaces the displayed generations.
            // The old consent must fail even when the proposed filenames still match.
            var replacementPreview = await host.PreviewRewindImportAsync(pair.Id);
            True(replacementPreview.LocalPreviewScanId != preview.LocalPreviewScanId);
            await ThrowsAsync<InvalidOperationException>(() => host.ApplyRewindImportAsync(pair.Id, confirmed));
            Equal(downloadsBeforeConsentChecks, remote.DownloadCount);
            Equal(replacementPreview.LocalPreviewScanId, (await rewindStore.LoadActiveAsync(pair.Id))!.LocalPreviewScanId);
            preview = replacementPreview;
            confirmed = SyncRewindApplyConfirmation.FromSession(preview);

            // Supply a replacement at the admission seam; the real guard must re-read
            // durable state and refuse without marking that replacement blocked.
            host.RewindApplyPhaseForTest = phase => phase == "confirmed_preview_admission"
                ? rewindStore.UpsertAsync(preview with { LocalPreviewScanId = "changed-at-run-admission" })
                : Task.CompletedTask;
            await ThrowsAsync<InvalidOperationException>(() => host.ApplyRewindImportAsync(pair.Id, confirmed));
            host.RewindApplyPhaseForTest = null;
            Equal(downloadsBeforeConsentChecks, remote.DownloadCount);
            Equal("changed-at-run-admission", (await rewindStore.LoadActiveAsync(pair.Id))!.LocalPreviewScanId);
            Equal(SyncRewindStatus.PreviewReady, (await rewindStore.LoadActiveAsync(pair.Id))!.Status);
            await rewindStore.UpsertAsync(preview);

            var downloadCountBeforeUnreviewedDrift = remote.DownloadCount;
            remote.Put("outside-preview.txt", "unreviewed-provider-change");
            var outsideBlocked = await host.ApplyRewindImportAsync(pair.Id, confirmed);
            Equal(SyncRewindStatus.ApplyBlocked, outsideBlocked.Session.Status);
            True(outsideBlocked.Result.Decisions.Any(item => item.RelativePath == "<rewind>" &&
                                                             item.Kind == SyncDecisionKind.Blocked));
            Equal(downloadCountBeforeUnreviewedDrift, remote.DownloadCount);
            True(!File.Exists(Path.Combine(localRoot, "outside-preview.txt")));
            remote.Delete("outside-preview.txt");

            remote.Put("stable.txt", "post-preview-provider-drift");
            var blocked = await host.ApplyRewindImportAsync(pair.Id, confirmed);
            Equal(SyncRewindStatus.ApplyBlocked, blocked.Session.Status);
            Equal("checkpoint-stable", File.ReadAllText(Path.Combine(localRoot, "stable.txt")));
            await ThrowsAsync<InvalidOperationException>(() => host.ResumeAfterRewindAsync(pair.Id));
            var refreshed = await host.PreviewRewindImportAsync(pair.Id);
            Equal(SyncRewindStatus.PreviewReady, refreshed.Status);
            True(refreshed.Preview!.Any(item => item.RelativePath == "stable.txt"));

            host.RewindApplyPhaseForTest = async phase =>
            {
                if (phase == "confirmed_preview_admission") return;
                Equal("applying_checkpoint", phase);
                True(await host.StopActiveRunAsync());
            };
            var stopped = await host.ApplyRewindImportAsync(pair.Id, SyncRewindApplyConfirmation.FromSession(refreshed));
            host.RewindApplyPhaseForTest = null;
            Equal(SyncRewindStatus.ApplyStopped, stopped.Session.Status);
            await ThrowsAsync<InvalidOperationException>(() => host.ResumeAfterRewindAsync(pair.Id));

            await new SyncRewindStore(authority).UpsertAsync(
                stopped.Session with { Status = SyncRewindStatus.Applying, Detail = "simulated process interruption" });
        }

        using (var restarted = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient, pairStore,
                   new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                   authority, (_, _) => remote))
        {
            var retainedPreview = (await restarted.LoadRewindSessionsAsync()).Single(item => item.PairId == pair.Id &&
                item.Status != SyncRewindStatus.Resumed);
            var applied = await restarted.ApplyRewindImportAsync(pair.Id,
                SyncRewindApplyConfirmation.FromSession(retainedPreview));
            Equal(SyncRewindStatus.AppliedPaused, applied.Session.Status);
            Equal("post-preview-provider-drift", File.ReadAllText(Path.Combine(localRoot, "stable.txt")));
            Equal("rewind-restored", File.ReadAllText(Path.Combine(localRoot, "restored.txt")));
            Equal("post-checkpoint-local-work", File.ReadAllText(Path.Combine(localRoot, "concurrent.txt")));
            Equal("rewound-concurrent", remote.ReadText("concurrent.txt"));
            True(applied.Result.Conflicts >= 1);

            var stillPaused = await restarted.RunPairsAsync([pair], "Automatic monitor");
            Equal(1, stillPaused.Items.Single().Result!.Blocked);
            var unsafeProgress = applied.Session.PathProgress!.Select((item, index) => index == 0
                ? item with { Status = SyncRewindPathStatus.Pending, OperationId = null }
                : item).ToArray();
            await new SyncRewindStore(authority).UpsertAsync(applied.Session with
            {
                Status = SyncRewindStatus.AppliedPaused,
                PathProgress = unsafeProgress,
                Detail = "simulated incomplete safe-resume boundary"
            });
            await ThrowsAsync<InvalidOperationException>(() => restarted.ResumeAfterRewindAsync(pair.Id));
            await new SyncRewindStore(authority).UpsertAsync(applied.Session);
            var resumed = await restarted.ResumeAfterRewindAsync(pair.Id);
            Equal(SyncRewindStatus.Resumed, resumed.Status);
            True((await new SyncRewindStore(authority).LoadActiveAsync(pair.Id)) is null);

            _ = await restarted.PrepareRewindAsync(pair.Id);
            var emptyPreview = await restarted.PreviewRewindImportAsync(pair.Id);
            Equal(0, emptyPreview.Preview!.Count);
            var emptyApplied = await restarted.ApplyRewindImportAsync(pair.Id,
                SyncRewindApplyConfirmation.FromSession(emptyPreview));
            Equal(SyncRewindStatus.AppliedPaused, emptyApplied.Session.Status);
            Equal<string?>(null, emptyApplied.Session.OperationGroupId);
            Equal(0, emptyApplied.Session.PathProgress!.Count);
            Equal(SyncRewindStatus.Resumed, (await restarted.ResumeAfterRewindAsync(pair.Id)).Status);
        }
    });
}

static Task OfflineSelectivePolicyContract()
{
    return WithTempRoot(async root =>
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var localRoot = Path.Combine(root, "local");
        Write(localRoot, "select/child/child.txt", "child-content");
        Write(localRoot, "select/sibling/sibling.txt", "sibling-content");
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var installationId = await pairStore.GetOrCreateInstallationIdAsync();
        var binding = Binding().Normalize();
        SyncPair pair;
        using (var localLease = PilotDirectoryLease.Open(localRoot))
        {
            pair = new SyncPair("fedcba9876543210abcd", "Offline selection fixture", localLease.Path,
                localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId,
                "/OfflineSelection", "id:offline-selection", ConnectionId: binding.ConnectionId,
                InitialReviewApproved: true,
                RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                    "id:offline-selection-recovery", "root-namespace"));
            pair = await BindFixtureControlAsync(pair, localLease, installationId);
        }
        await pairStore.UpsertAsync(pair);
        var remote = new FakeSyncRemoteStore();
        remote.PutDirectory("select");
        remote.PutDirectory("select/child");
        remote.PutDirectory("select/sibling");
        remote.Put("select/child/child.txt", "child-content");
        remote.Put("select/sibling/sibling.txt", "sibling-content");
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(binding);
        var credentialStore = new MemoryDropboxRefreshTokenStore("offline-selection-token");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var httpClient = new HttpClient();
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, httpClient, pairStore,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
            authority, (_, _) => remote);
        await host.LoadPairsAsync();
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: 30));
        await authority.RetainPairDefinitionAsync(pair, select: true);
        var baseline = await host.RunPairsAsync([pair], "Fixture baseline");
        True(baseline.Items.Single().Result is { Conflicts: 0, Blocked: 0 },
            "Selective-sync baseline did not converge: " + JsonSerializer.Serialize(baseline.Items.Single()));

        await credentialStore.DeleteAsync();
        var excluded = await host.SetFolderSelectionAsync(pair.Id, "select", included: false,
            "dropbox:id:folder:select");
        var excludedPair = excluded.Pairs.Single(item => item.Id == pair.Id);
        var excludedPolicy = SyncSelectionPolicy.Compile(excludedPair.SelectionRules);
        True(!excludedPolicy.IsIncluded("select/child/child.txt"));
        True(!excludedPolicy.IsIncluded("select/sibling/sibling.txt"));

        var childIncluded = await host.SetFolderSelectionAsync(pair.Id, "select/child", included: true,
            "dropbox:id:folder:select/child");
        var selectedPair = childIncluded.Pairs.Single(item => item.Id == pair.Id);
        var childPolicy = SyncSelectionPolicy.Compile(selectedPair.SelectionRules);
        True(childPolicy.IsIncluded("select/child/child.txt"));
        True(!childPolicy.IsIncluded("select/sibling/sibling.txt"));

        Directory.Move(Path.Combine(localRoot, "select", "child"), Path.Combine(localRoot, "select", "renamed"));
        remote.MoveDirectory("select/child", "select/renamed");
        await credentialStore.WriteAsync("online-again-for-rename-refresh");
        var renameRefresh = await host.RunPairsAsync([selectedPair], "Manual");
        var renameItem = renameRefresh.Items.Single();
        var renameResult = renameItem.Result ?? throw new InvalidOperationException(
            $"Selection rename refresh failed: {renameItem.FailureType}: {renameItem.FailureMessage}");
        True(renameResult is { Conflicts: 0, Blocked: 0 },
            "Selection rename refresh produced: " + string.Join(" | ",
                renameResult.Decisions.Select(decision =>
                    $"{decision.RelativePath}:{decision.Kind}:{decision.Reason}")));
        var renamedPair = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        True((renamedPair.SelectionRules ?? []).Any(rule => rule.Included &&
            rule.FolderPath == "select/renamed" &&
            rule.StableFolderIdentity == "dropbox:id:folder:select/child"));

        Write(localRoot, "select/renamed/child.txt", "unsynchronized-offline-work");
        await credentialStore.DeleteAsync();
        await ThrowsAsync<InvalidOperationException>(() =>
            host.SetFolderSelectionAsync(pair.Id, "select/renamed", included: false,
                "dropbox:id:folder:select/child"));
        var retained = (await host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        True(SyncSelectionPolicy.Compile(retained.SelectionRules).IsIncluded("select/renamed/child.txt"));
    });
}

static async Task DropboxUploadSessionOffsetRecovery()
{
    var bytes = Bytes("0123456789abcdefghij");
    await using var hashSource = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(hashSource);
    var handler = new DropboxUploadSessionContractHandler { LoseNextAppendAfterAccept = true };
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder", uploadSessionThresholdBytes: 4, uploadSessionChunkSizeBytes: 8);
    await client.VerifyRootIdentityAsync();
    SyncUploadSessionCheckpoint? checkpoint = null;
    await using (var interrupted = new MemoryStream(bytes, writable: false))
        await ThrowsAsync<DropboxEffectUncertainException>(() => client.UploadResumableAsync(
            "large.bin", interrupted, hash, null, checkpoint,
            (value, _) =>
            {
                checkpoint = value;
                return Task.CompletedTask;
            }));
    Equal(8L, checkpoint?.ConfirmedOffset);

    await using var resumed = new MemoryStream(bytes, writable: false);
    var result = await client.UploadResumableAsync("large.bin", resumed, hash, null, checkpoint,
        (value, _) =>
        {
            checkpoint = value;
            return Task.CompletedTask;
        });
    True(handler.CommittedBytes.SequenceEqual(bytes));
    Equal(1, handler.StartCount);
    Equal(1, handler.CorrectOffsetCount);
    Equal(bytes.Length, result.Size);
    Equal(hash, result.ContentHash);
}

static async Task DropboxUploadSessionExpiryRecovery()
{
    var bytes = Bytes("0123456789abcdefghij");
    await using var hashSource = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(hashSource);
    var handler = new DropboxUploadSessionContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder", uploadSessionThresholdBytes: 4, uploadSessionChunkSizeBytes: 8);
    SyncUploadSessionCheckpoint? checkpoint = null;
    await using (var interrupted = new MemoryStream(bytes, writable: false))
        await ThrowsAsync<IOException>(() => client.UploadResumableAsync("expired.bin", interrupted,
            hash, null, null, (value, _) =>
            {
                checkpoint = value;
                throw new IOException("simulated process loss after durable start checkpoint");
            }));
    handler.ExpireSessions();
    await using var resumed = new MemoryStream(bytes, writable: false);
    await client.UploadResumableAsync("expired.bin", resumed, hash, null, checkpoint,
        (value, _) =>
        {
            checkpoint = value;
            return Task.CompletedTask;
        });
    True(handler.CommittedBytes.SequenceEqual(bytes));
    Equal(2, handler.StartCount);
    True(checkpoint is not null && checkpoint.ConfirmedOffset == 16);
}

static async Task DropboxUploadSessionFinishUncertain()
{
    var bytes = Bytes("0123456789abcdefghij");
    await using var hashSource = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(hashSource);
    var handler = new DropboxUploadSessionContractHandler { LoseNextFinishAfterAccept = true };
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
        "id:pilot-folder", uploadSessionThresholdBytes: 4, uploadSessionChunkSizeBytes: 8);
    SyncUploadSessionCheckpoint? checkpoint = null;
    await using (var first = new MemoryStream(bytes, writable: false))
        await ThrowsAsync<DropboxEffectUncertainException>(() => client.UploadResumableAsync(
            "uncertain.bin", first, hash, null, checkpoint, (value, _) =>
            {
                checkpoint = value;
                return Task.CompletedTask;
            }));
    True(handler.CommittedBytes.SequenceEqual(bytes));
    Equal(16L, checkpoint?.ConfirmedOffset);

    await using var unsafeRetry = new MemoryStream(bytes, writable: false);
    await ThrowsAsync<DropboxEffectUncertainException>(() => client.UploadResumableAsync(
        "uncertain.bin", unsafeRetry, hash, null, checkpoint, static (_, _) => Task.CompletedTask));
    Equal(1, handler.StartCount);
    Equal(2, handler.FinishCount);
}

static async Task DropboxDownloadWireContract()
{
    var handler = new DropboxContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    await client.VerifyRootIdentityAsync();
    var bytes = Bytes("download wire bytes");
    await using var hashSource = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(hashSource);
    handler.DownloadBytes = bytes;
    handler.DownloadHash = hash;
    handler.DownloadRevision = "download-revision";
    handler.MetadataRevision = "download-revision";
    handler.ArchiveHash = hash;
    handler.ArchiveSize = bytes.Length;
    await using var destination = new MemoryStream();
    var result = await client.DownloadAsync(new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "download-revision",
        bytes.Length, hash), destination);
    Equal("rev:download-revision", handler.DownloadPath);
    Equal(null, handler.DownloadRequestedRevision);
    True(handler.DownloadArgumentProperties.SequenceEqual(["path"], StringComparer.Ordinal));
    Equal(hash, result.ContentHash);
    Equal("download wire bytes", Encoding.UTF8.GetString(destination.ToArray()));
    // The revision-pinned download response carries the whole source identity: one call, no per-file metadata.
    Equal(1, handler.DownloadRequests);
    Equal(0, handler.WireMetadataRequests);

    var pathlessHandler = new DropboxContractHandler { OmitDownloadMetadataPaths = true };
    using (var pathlessHttp = new HttpClient(pathlessHandler))
    {
        var pathlessClient = new DropboxPilotClient(pathlessHttp, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        pathlessHandler.DownloadBytes = bytes;
        pathlessHandler.DownloadHash = hash;
        pathlessHandler.DownloadRevision = "download-revision";
        pathlessHandler.MetadataRevision = "download-revision";
        pathlessHandler.ArchiveHash = hash;
        pathlessHandler.ArchiveSize = bytes.Length;
        await using var pathlessDestination = new MemoryStream();
        var pathless = await pathlessClient.DownloadAsync(new PilotRemoteEntry("wire.txt",
            PilotEntryKind.File, "id:wire", "download-revision", bytes.Length, hash), pathlessDestination);
        Equal(hash, pathless.ContentHash);
        Equal("download wire bytes", Encoding.UTF8.GetString(pathlessDestination.ToArray()));
    }

    var mismatchedHandler = new DropboxContractHandler { OmitDownloadMetadataPaths = true };
    using (var mismatchedHttp = new HttpClient(mismatchedHandler))
    {
        var mismatchedClient = new DropboxPilotClient(mismatchedHttp, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        mismatchedHandler.DownloadBytes = bytes;
        mismatchedHandler.DownloadHash = hash;
        mismatchedHandler.DownloadRevision = "different-revision";
        mismatchedHandler.MetadataRevision = "download-revision";
        mismatchedHandler.ArchiveHash = hash;
        mismatchedHandler.ArchiveSize = bytes.Length;
        await using var mismatchedDestination = new MemoryStream();
        await ThrowsAsync<IOException>(() => mismatchedClient.DownloadAsync(new PilotRemoteEntry("wire.txt",
            PilotEntryKind.File, "id:wire", "download-revision", bytes.Length, hash), mismatchedDestination));
    }
}

static async Task DropboxControlNamespaceMarkerWireContract()
{
    const string installationId = "installation-marker-01";
    const string namespaceId = "namespace-marker-0001";
    var rootPath = SyncControlNamespace.RemoteContainerPath + "/" + namespaceId;
    var markerPath = rootPath + "/.syncshark-owner.json";
    var markerBytes = SyncControlNamespace.MarkerBytes(installationId, namespaceId);
    var markerHash = DropboxContentHash.Compute(markerBytes);
    var binding = new SyncRemoteControlNamespaceBinding(rootPath, "id:control-root", markerPath,
        "id:control-marker", "control-marker", markerHash);

    var successHandler = new DropboxControlNamespaceMarkerHandler(rootPath, markerPath, markerBytes, markerHash,
        "success");
    using (var successHttp = new HttpClient(successHandler))
    {
        var client = new DropboxPilotClient(successHttp, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await client.VerifyControlNamespaceAsync(binding, installationId, namespaceId);
        Equal(1, successHandler.ControlRootMetadataCalls);
        Equal(1, successHandler.MarkerMetadataCalls);
        Equal(1, successHandler.DownloadCalls);
        Equal(0, successHandler.PairRootMetadataCalls);
        Equal("rev:control-marker", successHandler.DownloadPath);
    }

    foreach (var mode in new[] { "moved-metadata", "moved-download", "corrupt-bytes" })
    {
        var handler = new DropboxControlNamespaceMarkerHandler(rootPath, markerPath, markerBytes, markerHash, mode);
        using var http = new HttpClient(handler);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await ThrowsAsync<IOException>(() =>
            client.VerifyControlNamespaceAsync(binding, installationId, namespaceId));
        Equal(0, handler.PairRootMetadataCalls);
    }
}

static async Task DropboxArchiveWireContract()
{
    var handler = new DropboxContractHandler();
    using var httpClient = new HttpClient(handler);
    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(), "id:pilot-folder");
    await client.VerifyRootIdentityAsync();
    var bytes = Bytes("archive wire bytes");
    await using var hashSource = new MemoryStream(bytes, writable: false);
    var hash = await DropboxContentHash.ComputeAsync(hashSource);
    handler.ArchiveHash = hash;
    handler.ArchiveSize = bytes.Length;
    handler.DownloadBytes = bytes;
    handler.DownloadHash = hash;
    handler.DownloadRevision = "archive-revision";
    handler.MetadataRevision = "archive-revision";
    const string recoveryPath = "/Apps/Sync Shark/Recovery/pair-identifier/0123456789abcdef0123456789abcdef";
    var result = await client.CopyToRecoveryAsync(new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "archive-revision",
        bytes.Length, hash), recoveryPath);
    Equal("rev:archive-revision", handler.DownloadPath);
    Equal(null, handler.DownloadRequestedRevision);
    True(handler.DownloadArgumentProperties.SequenceEqual(["path"], StringComparer.Ordinal));
    Equal(recoveryPath, handler.UploadPath);
    Equal("add", handler.UploadMode);
    Equal(true, handler.UploadStrictConflict);
    Equal(hash, result.ContentHash);
    Equal(recoveryPath, result.RecoveryPath);
}

static Task MultiplePairPersistence()
{
    return WithTempRoot(async root =>
    {
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var legacy = Settings(firstRoot);
        await new PilotSettingsStore(appLock.StateAuthority).SaveAsync(legacy);
        var store = new SyncPairStore(appLock.StateAuthority);
        var migrated = store.Load();
        Equal(1, migrated.Pairs.Count);
        Equal(legacy.DropboxFolderId, migrated.Pairs[0].DropboxFolderId);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        var second = SyncPair.FromPilot(new PilotSettings(secondLease.Path, secondLease.Identity.VolumeSerialNumber,
            secondLease.Identity.FileId, "/Second", "id:second"), "Second pair");
        var saved = await store.UpsertAsync(second);
        Equal(2, saved.Pairs.Count);
        Equal(second.Id, saved.SelectedPairId);
        Equal(2, store.Load().Pairs.Count);
    });
}

static Task RecoveryFirstMultiPairOrdering()
{
    return WithTempRoot(async root =>
    {
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var firstLease = PilotDirectoryLease.Open(firstRoot);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        var first = SyncPair.FromPilot(new PilotSettings(firstLease.Path, firstLease.Identity.VolumeSerialNumber,
            firstLease.Identity.FileId, "/First", "id:first"), "First recovery pair");
        var second = SyncPair.FromPilot(new PilotSettings(secondLease.Path, secondLease.Identity.VolumeSerialNumber,
            secondLease.Identity.FileId, "/Second", "id:second"), "Second recovery pair");
        var pairs = new SyncPairStore(appLock.StateAuthority);
        await pairs.UpsertAsync(first);
        await pairs.UpsertAsync(second);
        var runtime = new SyncRuntimeStateStore(appLock.StateAuthority);

        static SyncTransferIntent Pending(SyncPair pair, DateTimeOffset started, char hashDigit)
        {
            var operationId = Guid.NewGuid().ToString("N");
            var hash = new string(hashDigit, 64);
            return new SyncTransferIntent(operationId, pair.Id, "pending.txt", SyncDirection.Upload,
                hash, hash, "rev:pending", null, null, started,
                SyncIntentKind.ArchiveRemoteAfterLocalDeletion,
                RecoveryPath: $"/Apps/Sync Shark/Recovery/{pair.Id}/{operationId}",
                RecoveryOriginalPath: "pending.txt", ExpectedSourceId: "id:pending",
                ExpectedSourceRevision: "rev:pending", ExpectedSourceSize: 7,
                CreationPolicyRevision: pair.PolicyRevision);
        }

        var older = Pending(first, DateTimeOffset.UtcNow.AddMinutes(-2), 'a');
        var newer = Pending(second, DateTimeOffset.UtcNow.AddMinutes(-1), 'b');
        await runtime.SavePairAsync(new SyncPairRuntimeState(first.Id, true, [], older));
        await runtime.SavePairAsync(new SyncPairRuntimeState(second.Id, true, [], newer));
        Equal(first.Id, runtime.LoadRecoveryOrder()[0]);
        Equal(second.Id, runtime.LoadRecoveryOrder()[1]);

        var active = await runtime.BeginRunAsync(second.Id, second.PolicyRevision);
        var withAbandonedActive = runtime.LoadRecoveryOrder();
        Equal(second.Id, withAbandonedActive[0]);
        Equal(first.Id, withAbandonedActive[1]);
        await runtime.EndRunAsync(active);
    });
}

static Task AggregateMigrationRecovery()
{
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path, localLease.Identity.VolumeSerialNumber,
            localLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Migrated pair");
        await new DualSlotJsonStore<SyncPairSet>(appLock.StateAuthority, SyncSharkRuntimePaths.PairSetStateName)
            .SaveAsync(new SyncPairSet([pair], pair.Id));
        await new DualSlotJsonStore<SyncRuntimeState>(appLock.StateAuthority, SyncSharkRuntimePaths.RuntimeStateName)
            .SaveAsync(new SyncRuntimeState([new SyncPairRuntimeState(pair.Id, true,
                [new SyncFileBaseline("retained.txt", "local", 5, "remote", 5, "id:retained", "rev:retained")], null)]));

        var pairs = new SyncPairStore(appLock.StateAuthority).Load();
        var runtime = new SyncRuntimeStateStore(appLock.StateAuthority).LoadPair(pair.Id);
        Equal(pair.Id, pairs.SelectedPairId);
        Equal(true, runtime.BaselineEstablished);
        Equal("retained.txt", runtime.Files.Single().RelativePath);
        True(File.Exists(Path.Combine(appLock.StateAuthority.StateRoot,
            SyncSharkRuntimePaths.ProductStateName + "-a.json")));
    });
}

static async Task AggregateCorruptStateRefusal()
{
    foreach (var corruptA in new[] { true, false })
    foreach (var bothInvalid in new[] { false, true })
        await WithTempRoot(async root =>
        {
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            var a = Path.Combine(appLock.StateAuthority.StateRoot, SyncSharkRuntimePaths.ProductStateName + "-a.json");
            var b = Path.Combine(appLock.StateAuthority.StateRoot, SyncSharkRuntimePaths.ProductStateName + "-b.json");
            var damaged = corruptA ? a : b;
            var other = corruptA ? b : a;
            await File.WriteAllTextAsync(damaged, "{torn");
            if (bothInvalid) await File.WriteAllTextAsync(other, "{also-torn");
            var preimage = await File.ReadAllBytesAsync(damaged);
            var otherPreimage = bothInvalid ? await File.ReadAllBytesAsync(other) : null;
            var product = new SyncProductStateStore(appLock.StateAuthority);
            Throws<InvalidDataException>(() => product.Load());
            await ThrowsAsync<InvalidDataException>(() => product.UpdateAsync(state => state));
            True(preimage.SequenceEqual(await File.ReadAllBytesAsync(damaged)));
            if (otherPreimage is null) True(!File.Exists(other));
            else True(otherPreimage.SequenceEqual(await File.ReadAllBytesAsync(other)));
        });

    foreach (var validA in new[] { true, false })
        await WithTempRoot(async root =>
        {
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            var product = new SyncProductStateStore(appLock.StateAuthority);
            var initial = product.Load();
            True(!string.IsNullOrWhiteSpace(initial.InstallationId));
            var a = Path.Combine(appLock.StateAuthority.StateRoot, SyncSharkRuntimePaths.ProductStateName + "-a.json");
            var b = Path.Combine(appLock.StateAuthority.StateRoot, SyncSharkRuntimePaths.ProductStateName + "-b.json");
            if (!validA) await File.WriteAllBytesAsync(b, await File.ReadAllBytesAsync(a));
            await File.WriteAllTextAsync(validA ? b : a, "{torn");
            Equal(initial.InstallationId, new SyncProductStateStore(appLock.StateAuthority).Load().InstallationId);
        });
}

static Task PairIdentityRebinding()
{
    return WithTempRoot(async root =>
    {
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var firstLease = PilotDirectoryLease.Open(firstRoot);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        var first = SyncPair.FromPilot(new PilotSettings(firstLease.Path, firstLease.Identity.VolumeSerialNumber,
            firstLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "First identity");
        var replacement = SyncPair.FromPilot(new PilotSettings(secondLease.Path, secondLease.Identity.VolumeSerialNumber,
            secondLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Rebound identity");
        True(!string.Equals(first.Id, replacement.Id, StringComparison.Ordinal));

        var pairs = new SyncPairStore(appLock.StateAuthority);
        var runtime = new SyncRuntimeStateStore(appLock.StateAuthority);
        await pairs.UpsertAsync(first);
        await runtime.SavePairAsync(new SyncPairRuntimeState(first.Id, true,
            [new SyncFileBaseline("old.txt", "local", 3, "remote", 3, "id:old", "rev:old")], null));

        var saved = await pairs.UpsertAsync(replacement, replacingPairId: first.Id);
        Equal(1, saved.Pairs.Count);
        Equal(replacement.Id, saved.Pairs[0].Id);
        Equal(replacement.Id, saved.SelectedPairId);
        Equal(false, runtime.LoadPair(replacement.Id).BaselineEstablished);
        Equal(0, runtime.Load().Pairs.Count);
    });
}

static Task PairPolicyAndRunFence()
{
    return WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path, localLease.Identity.VolumeSerialNumber,
            localLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Policy pair");
        var pairs = new SyncPairStore(appLock.StateAuthority);
        var runtime = new SyncRuntimeStateStore(appLock.StateAuthority);
        await pairs.UpsertAsync(pair);
        var baseline = new SyncPairRuntimeState(pair.Id, true,
            [new SyncFileBaseline("retained.txt", "local", 5, "remote", 5, "id:retained", "rev:retained")], null);
        await runtime.SavePairAsync(baseline);

        var beforeNoOp = Directory.EnumerateFiles(appLock.StateAuthority.StateRoot,
                SyncSharkRuntimePaths.ProductStateName + "-*.json")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var unchanged = await pairs.UpsertAsync(pair, replacingPairId: pair.Id);
        Equal(1L, unchanged.Pairs.Single().PolicyRevision);
        var afterNoOp = Directory.EnumerateFiles(appLock.StateAuthority.StateRoot,
                SyncSharkRuntimePaths.ProductStateName + "-*.json")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Equal(beforeNoOp.Count, afterNoOp.Count);
        foreach (var item in beforeNoOp) True(afterNoOp[item.Key].SequenceEqual(item.Value));

        var policy = await pairs.UpsertAsync(pair with { Priority = 7, PropagateDeletions = true },
            replacingPairId: pair.Id);
        var revised = policy.Pairs.Single();
        Equal(2L, revised.PolicyRevision);
        Equal(7, revised.Priority);
        Equal("retained.txt", runtime.LoadPair(pair.Id).Files.Single().RelativePath);

        var lease = await runtime.BeginRunAsync(pair.Id, revised.PolicyRevision);
        var persisted = new DualSlotJsonStore<SyncProductState>(appLock.StateAuthority,
            SyncSharkRuntimePaths.ProductStateName).Load()!;
        Equal(lease.Generation, persisted.ActiveRun!.Generation);
        await ThrowsAsync<InvalidOperationException>(() => pairs.UpsertAsync(revised with { Priority = 8 },
            replacingPairId: pair.Id));
        await ThrowsAsync<InvalidOperationException>(() => pairs.SaveAsync(new SyncPairSet([], null)));
        Equal(1, pairs.Load().Pairs.Count);
        await runtime.EndRunAsync(lease);

        var pending = new SyncTransferIntent(Guid.NewGuid().ToString("N"), pair.Id, "retained.txt",
            SyncDirection.Upload, "local", "remote", "rev:retained", null, null, DateTimeOffset.UtcNow,
            ExpectedSourceId: "00000001:0000000000000001", ExpectedSourceSize: 5,
            CreationPolicyRevision: revised.PolicyRevision,
            ExpectedSourceVolumeSerialNumber: 1, ExpectedSourceFileId: 1,
            ExpectedSourceLastWriteUtc: DateTime.UnixEpoch,
            ExpectedDestinationRemoteId: "id:retained", ExpectedDestinationSize: 5,
            RecoveryPath: "/Apps/Sync Shark/Recovery/" + pair.Id + "/0123456789abcdef0123456789abcdef",
            RecoveryOriginalPath: "retained.txt");
        await runtime.SavePairAsync(baseline with { PendingTransfer = pending });
        await ThrowsAsync<InvalidOperationException>(() => pairs.UpsertAsync(revised with { Priority = 9 },
            replacingPairId: pair.Id));
        Equal(7, pairs.Load().Pairs.Single().Priority);
        await runtime.SavePairAsync(baseline);
        var afterRecovery = await pairs.UpsertAsync(revised with { Priority = 9 }, replacingPairId: pair.Id);
        Equal(3L, afterRecovery.Pairs.Single().PolicyRevision);
        Equal("retained.txt", runtime.LoadPair(pair.Id).Files.Single().RelativePath);
    });
}

static Task HeldLocalObjectMutation()
{
    return WithTempRoot(root =>
    {
        using var rootLease = PilotDirectoryLease.Open(root);
        var source = Path.Combine(root, "source.txt");
        var displaced = Path.Combine(root, "displaced.txt");
        var committed = Path.Combine(root, "committed.txt");
        File.WriteAllText(source, "authoritative-object");
        using (var held = SyncLocalFileAuthority.OpenExisting(source))
        {
            File.Move(source, displaced);
            File.WriteAllText(source, "pathname-substitute");
            held.RenameTo(rootLease, "committed.txt");
        }
        Equal("authoritative-object", File.ReadAllText(committed));
        Equal("pathname-substitute", File.ReadAllText(source));
        Equal(false, File.Exists(displaced));

        var artifact = Path.Combine(root, "artifact.partial");
        var movedArtifact = Path.Combine(root, "moved-artifact.partial");
        File.WriteAllText(artifact, "owned-artifact");
        using (var heldArtifact = SyncLocalFileAuthority.OpenExisting(artifact))
        {
            File.Move(artifact, movedArtifact);
            File.WriteAllText(artifact, "foreign-replacement");
            heldArtifact.DeleteOnClose();
        }
        Equal("foreign-replacement", File.ReadAllText(artifact));
        Equal(false, File.Exists(movedArtifact));
        return Task.CompletedTask;
    });
}

static Task BidirectionalBaseline()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "local-only.txt", "local-before-baseline");
        fixture.Remote.Put("remote-only.txt", "remote-before-baseline");
        var first = await new BidirectionalSyncEngine(fixture.Pair, fixture.Remote, fixture.StateStore,
            new PilotRunControl()).RunAsync();
        Equal(0, first.Uploaded);
        Equal(0, first.Downloaded);
        Equal(0, fixture.Remote.UploadCount);
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "remote-only.txt")));
        var firstState = fixture.StateStore.LoadPair(fixture.Pair.Id);
        Equal(false, firstState.BaselineEstablished);
        Equal(1, firstState.InitialObservationCount);
        Equal(2, firstState.InitialObservation!.Count);

        var second = await new BidirectionalSyncEngine(fixture.Pair, fixture.Remote, fixture.StateStore,
            new PilotRunControl()).RunAsync();
        Equal(0, second.Uploaded);
        Equal(0, second.Downloaded);
        var state = fixture.StateStore.LoadPair(fixture.Pair.Id);
        Equal(true, state.BaselineEstablished);
        Equal(2, state.Files.Count);
    });
}

static Task BidirectionalNewFiles()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "upload.txt", "new-local");
        fixture.Remote.Put("download.txt", "new-remote");
        var result = await fixture.RunAsync();
        Equal(1, result.Uploaded);
        Equal(1, result.Downloaded);
        Equal("new-local", fixture.Remote.ReadText("upload.txt"));
        Equal("new-remote", File.ReadAllText(Path.Combine(fixture.LocalRoot, "download.txt")));
        var rerun = await fixture.RunAsync();
        Equal(0, rerun.Uploaded);
        Equal(0, rerun.Downloaded);
    });
}

static Task CreateNewCollisionPreserved()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        fixture.Remote.Put("collision.txt", "dropbox-source");
        SyncLocalTransfer.BeforeTargetCommitForTest = target => File.WriteAllText(target, "unknown-local-occupant");
        SyncRunResult collided;
        try { collided = await fixture.RunAsync(); }
        finally { SyncLocalTransfer.BeforeTargetCommitForTest = null; }

        Equal(1, collided.Blocked);
        Equal("unknown-local-occupant", File.ReadAllText(Path.Combine(fixture.LocalRoot, "collision.txt")));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);

        var nextRun = await fixture.RunAsync();
        Equal(1, nextRun.Blocked);
        Equal("unknown-local-occupant", File.ReadAllText(Path.Combine(fixture.LocalRoot, "collision.txt")));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);
    });
}

static async Task TrueSyncCapabilityPreflight()
{
    Equal(true, SyncCapabilityGate.CreateNewUpload);
    Equal(true, SyncCapabilityGate.CreateNewDownload);
    Equal(true, SyncCapabilityGate.ExistingFileReplacement);
    Equal(true, SyncCapabilityGate.ConflictResolution);
    Equal(true, SyncCapabilityGate.DeletionPropagation);

    await WithSyncFixture(async fixture =>
    {
        Directory.CreateDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        var preexistingStaging = Directory.EnumerateFiles(SyncSharkRuntimePaths.ReplacementStagingRoot, "*",
            SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();

        Write(fixture.LocalRoot, "shared.txt", "local-update");
        var uploaded = await fixture.RunAsync();
        Equal(1, uploaded.Uploaded);
        Equal("local-update", fixture.Remote.ReadText("shared.txt"));

        fixture.Remote.Put("shared.txt", "remote-update");
        var writeFenceRejected = false;
        SyncLocalTransfer.TargetPromotedForTest = path =>
        {
            try { File.WriteAllText(path, "unfenced-write"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { writeFenceRejected = true; }
        };
        SyncRunResult downloaded;
        try { downloaded = await fixture.RunAsync(); }
        finally { SyncLocalTransfer.TargetPromotedForTest = null; }
        Equal(1, downloaded.Downloaded);
        True(writeFenceRejected);
        Equal("remote-update", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        var currentStaging = Directory.EnumerateFiles(SyncSharkRuntimePaths.ReplacementStagingRoot, "*",
            SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Equal(true, preexistingStaging.SetEquals(currentStaging));

        Write(fixture.LocalRoot, "shared.txt", "local-conflict");
        fixture.Remote.Put("shared.txt", "remote-conflict");
        var conflict = await fixture.RunAsync();
        Equal(1, conflict.Conflicts);
        Equal("local-conflict", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        Equal("remote-conflict", fixture.Remote.ReadText("shared.txt"));

        Write(fixture.LocalRoot, "delete.txt", "delete-baseline");
        fixture.Remote.Put("delete.txt", "delete-baseline");
        await fixture.RunAsync();
        File.Delete(Path.Combine(fixture.LocalRoot, "delete.txt"));
        var deletion = await fixture.RunAsync();
        Equal(true, fixture.Remote.Exists("delete.txt"));
        True(deletion.Decisions.Any(item => item.RelativePath == "delete.txt" && item.Kind == SyncDecisionKind.Conflict));

        await VerifyPrivateReplacementRestartMatrix(fixture.LocalRoot);
    });
}

static async Task VerifyPrivateReplacementRestartMatrix(string localRoot)
{
    Directory.CreateDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
    using var rootLease = PilotDirectoryLease.Open(localRoot);
    var sourceBytes = Bytes("restart-source");
    var sourceHash = await HashBytesAsync(sourceBytes);
    var baselineHash = await HashBytesAsync(Bytes("restart-baseline"));
    var remote = new PilotRemoteEntry("restart.txt", PilotEntryKind.File, "id:restart", "rev:restart",
        sourceBytes.Length, sourceHash);

    async Task<SyncTransferIntent> Prepared(string targetName)
    {
        var targetPath = Path.Combine(localRoot, targetName);
        File.WriteAllText(targetPath, "restart-baseline");
        await using var target = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
        var identity = target.VerifySingleLink();
        var id = Guid.NewGuid().ToString("N");
        var intent = new SyncTransferIntent(id, "restart-pair", targetName, SyncDirection.Download, sourceHash,
            baselineHash, remote.Revision, null, null, DateTimeOffset.UtcNow, CreationPolicyRevision: 1,
            ExpectedDestinationVolumeSerialNumber: identity.VolumeSerialNumber,
            ExpectedDestinationFileId: identity.FileId, StagingRelativePath: SyncPrivateReplacement.RelativeStage(id));
        return SyncPrivateReplacement.BindStagingRoot(intent);
    }

    var noStage = await Prepared("restart-none.txt");
    var notApplied = await SyncPrivateReplacement.RecoverAsync(remote with { RelativePath = noStage.RelativePath },
        rootLease, noStage, static (_, _) => Task.CompletedTask, CancellationToken.None);
    Equal(SyncLocalPendingDisposition.NotApplied, notApplied.Disposition);

    var persistFailure = await Prepared("restart-persist-failure.txt");
    var persistRemote = new FakeSyncRemoteStore();
    persistRemote.Put(persistFailure.RelativePath, "restart-source");
    var persistEntry = (await persistRemote.ListEntriesAsync()).Single(item =>
        item.RelativePath == persistFailure.RelativePath);
    await ThrowsAsync<IOException>(() => SyncPrivateReplacement.DownloadAsync(persistEntry,
        new PilotLocalEntry(persistFailure.RelativePath, PilotEntryKind.File,
            Path.Combine(localRoot, persistFailure.RelativePath), Bytes("restart-baseline").Length,
            File.GetLastWriteTimeUtc(Path.Combine(localRoot, persistFailure.RelativePath)), baselineHash,
            persistFailure.ExpectedDestinationVolumeSerialNumber, persistFailure.ExpectedDestinationFileId),
        rootLease, persistRemote, persistFailure,
        static (_, _) => throw new IOException("fixture durable stage binding failure"), CancellationToken.None));
    True(!File.Exists(Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot,
        SyncPrivateReplacement.RelativeStage(persistFailure.Id))));
    Equal("restart-baseline", File.ReadAllText(Path.Combine(localRoot, persistFailure.RelativePath)));

    var unknown = await Prepared("restart-unknown.txt");
    var unknownStage = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot,
        SyncPrivateReplacement.RelativeStage(unknown.Id));
    File.WriteAllText(unknownStage, "unknown-occupant");
    var blocked = await SyncPrivateReplacement.RecoverAsync(remote with { RelativePath = unknown.RelativePath },
        rootLease, unknown, static (_, _) => Task.CompletedTask, CancellationToken.None);
    Equal(SyncLocalPendingDisposition.Blocked, blocked.Disposition);
    Equal("unknown-occupant", File.ReadAllText(unknownStage));
    File.Delete(unknownStage);

    var mismatched = await Prepared("restart-stage-id-mismatch.txt");
    var mismatchedStagePath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot,
        SyncPrivateReplacement.RelativeStage(mismatched.Id));
    await using (var mismatchStage = SyncLocalFileAuthority.CreateNewFile(mismatchedStagePath))
    {
        await mismatchStage.Stream.WriteAsync(sourceBytes);
        mismatchStage.Stream.Flush(true);
    }
    mismatched = mismatched with { StageArtifactId = "00000001:0000000000000001" };
    var mismatchResult = await SyncPrivateReplacement.RecoverAsync(
        remote with { RelativePath = mismatched.RelativePath }, rootLease, mismatched,
        static (_, _) => Task.CompletedTask, CancellationToken.None);
    Equal(SyncLocalPendingDisposition.Blocked, mismatchResult.Disposition);
    Equal("restart-baseline", File.ReadAllText(Path.Combine(localRoot, mismatched.RelativePath)));
    True(File.Exists(mismatchedStagePath));
    File.Delete(mismatchedStagePath);

    var staged = await Prepared("restart-staged.txt");
    var stagePath = Path.Combine(SyncSharkRuntimePaths.ReplacementStagingRoot,
        SyncPrivateReplacement.RelativeStage(staged.Id));
    await using (var stage = SyncLocalFileAuthority.CreateNewFile(stagePath))
    {
        await stage.Stream.WriteAsync(sourceBytes);
        stage.Stream.Flush(true);
        var identity = stage.VerifySingleLink();
        staged = staged with { StageArtifactId = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}" };
    }
    SyncTransferIntent? committedIntent = null;
    var committed = await SyncPrivateReplacement.RecoverAsync(remote with { RelativePath = staged.RelativePath },
        rootLease, staged, (value, _) => { committedIntent = value; return Task.CompletedTask; },
        CancellationToken.None);
    Equal(SyncLocalPendingDisposition.Committed, committed.Disposition);
    Equal("restart-source", File.ReadAllText(Path.Combine(localRoot, staged.RelativePath)));
    True(committedIntent?.Phase == SyncIntentPhase.CommittedCleanupPending);
    SyncPrivateReplacement.CleanupCommitted(committedIntent! with { Phase = SyncIntentPhase.CleanupCompleted });

    var rootProbe = await Prepared("restart-root-identity.txt");
    var stagingRoot = SyncSharkRuntimePaths.ReplacementStagingRoot;
    var displacedRoot = stagingRoot + ".identity-test-" + Guid.NewGuid().ToString("N");
    Directory.Move(stagingRoot, displacedRoot);
    Directory.CreateDirectory(stagingRoot);
    try
    {
        await ThrowsAsync<IOException>(() => SyncPrivateReplacement.RecoverAsync(
            remote with { RelativePath = rootProbe.RelativePath }, rootLease, rootProbe,
            static (_, _) => Task.CompletedTask, CancellationToken.None));
    }
    finally
    {
        Directory.Delete(stagingRoot, recursive: false);
        Directory.Move(displacedRoot, stagingRoot);
    }
}

static Task TrueSyncProcessCrashRecovery()
{
    return WithTempRoot(async root =>
    {
        foreach (var boundary in new[] { "local-recovery-created", "stage-created", "download-verified",
                     "artifacts-bound", "predecessor-deleted", "target-promoted", "committed-before-return",
                     "cleanup-persisted", "cleanup-verified" })
        {
            var caseRoot = Path.Combine(root, boundary);
            Directory.CreateDirectory(caseRoot);
            NativeTestFiles.RunTrueSyncCrashChild(boundary, caseRoot);
            await WithTrueSyncCrashFixture(caseRoot, initialize: false, async fixture =>
            {
                var attemptReasons = new List<string>();
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var run = await fixture.RunAsync();
                    attemptReasons.Add(string.Join(" | ", run.Decisions.Select(item => $"{item.Kind}:{item.Reason}")));
                    var controlRoot = fixture.Pair.ControlNamespace!.Local.RootPath;
                    var deletionConverged = boundary != "local-recovery-created" ||
                        !File.Exists(Path.Combine(fixture.LocalRoot, "archive-on-crash.txt")) &&
                        Directory.EnumerateDirectories(controlRoot, ".syncshark-recovery-*")
                            .SelectMany(Directory.EnumerateFiles)
                            .Any(path => File.ReadAllText(path) == "archive-on-crash");
                    var converged = deletionConverged &&
                        File.Exists(Path.Combine(fixture.LocalRoot, "create-only.txt")) &&
                        File.ReadAllText(Path.Combine(fixture.LocalRoot, "create-only.txt")) == "create-only" &&
                        File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")) == "remote-update";
                    if (converged &&
                        fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null) break;
                }
                if (!File.Exists(Path.Combine(fixture.LocalRoot, "create-only.txt")) ||
                    !File.Exists(Path.Combine(fixture.LocalRoot, "shared.txt")))
                    throw new InvalidOperationException($"Crash boundary {boundary} did not converge: {string.Join(" || ", attemptReasons)}");
                var observedCreated = File.ReadAllText(Path.Combine(fixture.LocalRoot, "create-only.txt"));
                var observedReplacement = File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt"));
                if (!string.Equals("create-only", observedCreated, StringComparison.Ordinal) ||
                    !string.Equals("remote-update", observedReplacement, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Crash boundary {boundary} did not converge both download paths.");
                if (fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null)
                    throw new InvalidOperationException($"Crash boundary {boundary} converged bytes but retained a pending intent.");
            });
        }
    });
}

static async Task RunTrueSyncCrashProbeAsync(string boundary, string root)
{
    await WithTrueSyncCrashFixture(root, initialize: true, async fixture =>
    {
        var crashFixture = fixture;
        if (boundary == "local-recovery-created")
        {
            crashFixture = fixture with { Pair = fixture.Pair with { PropagateDeletions = true } };
            crashFixture.Remote.Delete("archive-on-crash.txt");
            SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = () => NativeTestFiles.TerminateCurrentProcess(199);
        }
        else if (boundary == "stage-created")
            SyncLocalTransfer.StageCreatedForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "download-verified")
            SyncLocalTransfer.DownloadVerifiedForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "artifacts-bound")
            SyncLocalTransfer.ArtifactsBoundForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "predecessor-deleted")
            SyncLocalTransfer.PredecessorDeletedForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "target-promoted")
            SyncLocalTransfer.TargetPromotedForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "committed-before-return")
            SyncLocalTransfer.CommittedBeforeReturnForTest = _ => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "cleanup-persisted")
            BidirectionalSyncEngine.CleanupStatePersistedForTest = (_, _) => NativeTestFiles.TerminateCurrentProcess(199);
        else if (boundary == "cleanup-verified")
            BidirectionalSyncEngine.CleanupArtifactsVerifiedForTest = (_, _) => NativeTestFiles.TerminateCurrentProcess(199);
        else throw new InvalidDataException("The true-sync crash boundary is not accepted.");
        await crashFixture.RunAsync();
        throw new InvalidOperationException($"The true-sync crash probe '{boundary}' did not terminate.");
    });
}

static async Task WithTrueSyncCrashFixture(string root, bool initialize, Func<SyncFixture, Task> action)
{
    var localRoot = Path.Combine(root, "local");
    Directory.CreateDirectory(localRoot);
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
    var remote = new FakeSyncRemoteStore();
    SyncPair pair;
    if (initialize)
    {
        using var lease = PilotDirectoryLease.Open(localRoot);
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        pair = SyncPair.FromPilot(new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Pilot", "id:pilot-folder"), "True-sync crash fixture");
        pair = await BindFixtureControlAsync(pair, lease,
            await pairStore.GetOrCreateInstallationIdAsync());
        pair = (await pairStore.UpsertAsync(pair)).Pairs.Single();
    }
    else pair = new SyncPairStore(appLock.StateAuthority).Load().Pairs.Single();
    remote.Put("shared.txt", initialize ? "baseline" : "remote-update");
    if (initialize) remote.Put("archive-on-crash.txt", "archive-on-crash");
    if (!initialize) remote.Put("create-only.txt", "create-only");
    var fixture = new SyncFixture(localRoot, pair, remote, new SyncRuntimeStateStore(appLock.StateAuthority));
    if (initialize)
    {
        Write(localRoot, "shared.txt", "baseline");
        Write(localRoot, "archive-on-crash.txt", "archive-on-crash");
        await fixture.RunAsync();
        remote.Put("create-only.txt", "create-only");
        remote.Put("shared.txt", "remote-update");
    }
    await action(fixture);
}

static Task BidirectionalModifications()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();

        Write(fixture.LocalRoot, "shared.txt", "local-new");
        var upload = await fixture.RunAsync();
        Equal(1, upload.Uploaded);
        Equal("local-new", fixture.Remote.ReadText("shared.txt"));

        fixture.Remote.Put("shared.txt", "remote-new");
        var download = await fixture.RunAsync();
        Equal(1, download.Downloaded);
        Equal("remote-new", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        Equal(0, Directory.EnumerateFiles(fixture.LocalRoot, ".syncshark-transfer-*").Count());
    });
}

static Task BidirectionalConflict()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "shared.txt", "local-version");
        fixture.Remote.Put("shared.txt", "remote-version");
        var result = await fixture.RunAsync();
        Equal(1, result.Conflicts);
        Equal("local-version", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        Equal("remote-version", fixture.Remote.ReadText("shared.txt"));
    });
}

static Task BidirectionalUploadRecovery()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "uncertain.txt", "accepted-once");
        fixture.Remote.InterruptNextUploadAfterEffect = true;
        var interrupted = await fixture.RunAsync();
        Equal(1, interrupted.Blocked);
        Equal(1, fixture.Remote.UploadCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);
        var recovered = await fixture.RunAsync();
        Equal(1, recovered.Recovered);
        Equal(1, fixture.Remote.UploadCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
    });
}

static Task BidirectionalUploadNoEffectRecovery()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "retry-once.txt", "source-remains");
        fixture.Remote.InterruptNextUploadBeforeEffect = true;
        var interrupted = await fixture.RunAsync();
        Equal(1, interrupted.Blocked);
        Equal(0, fixture.Remote.UploadCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);

        var inventoried = await fixture.RunAsync();
        Equal(1, inventoried.Recovered);
        Equal(0, fixture.Remote.UploadCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);

        var retried = await fixture.RunAsync();
        Equal(1, retried.Uploaded);
        Equal(1, fixture.Remote.UploadCount);
        Equal("source-remains", fixture.Remote.ReadText("retry-once.txt"));
    });
}

static Task BidirectionalDownloadRecovery()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        fixture.Remote.Put("retry.txt", "remote-source");
        fixture.Remote.InterruptNextDownload = true;
        var interrupted = await fixture.RunAsync();
        Equal(1, interrupted.Blocked);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);
        var inventoried = await fixture.RunAsync();
        True(inventoried.Recovered >= 1);
        Equal(0, inventoried.Downloaded);
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "retry.txt")));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
        var retried = await fixture.RunAsync();
        Equal(1, retried.Downloaded);
        Equal("remote-source", File.ReadAllText(Path.Combine(fixture.LocalRoot, "retry.txt")));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
    });
}

static Task DownloadStageHardLinkIsolation()
{
    return WithSyncFixture(async fixture =>
    {
        await fixture.RunAsync();
        fixture.Remote.Put("isolated.txt", "provider-secret-bytes");
        var aliasPath = Path.Combine(Path.GetDirectoryName(fixture.LocalRoot)!, "outside-alias.bin");
        var aliasCreated = false;
        var aliasRejected = false;
        SyncLocalTransfer.StageWriteIntervalForTest = stagePath =>
        {
            aliasCreated = NativeTestFiles.TryCreateHardLinkFromChild(aliasPath, stagePath);
            aliasRejected = !aliasCreated;
        };
        SyncRunResult result;
        try { result = await fixture.RunAsync(); }
        finally { SyncLocalTransfer.StageWriteIntervalForTest = null; }

        Equal(false, aliasCreated);
        Equal(true, aliasRejected);
        Equal(1, result.Downloaded);
        Equal(1, fixture.Remote.DownloadCount);
        True(!File.Exists(aliasPath));
    });
}

static Task RecoveryWriteHardLinkIsolation()
{
    return WithSyncFixture(async original =>
    {
        Write(original.LocalRoot, "shared.txt", "baseline");
        original.Remote.Put("shared.txt", "baseline");
        await original.RunAsync();

        original.Remote.Put("shared.txt", "remote-replacement");
        var replacementAlias = Path.Combine(Path.GetDirectoryName(original.LocalRoot)!, "replacement-alias.bin");
        var replacementRejected = false;
        SyncLocalTransfer.StageWriteIntervalForTest = stagePath =>
        {
            replacementRejected = !NativeTestFiles.TryCreateHardLinkFromChild(replacementAlias, stagePath);
        };
        try
        {
            var download = await original.RunAsync();
            Equal(1, download.Downloaded);
        }
        finally { SyncLocalTransfer.StageWriteIntervalForTest = null; }
        Equal(true, replacementRejected);
        True(!File.Exists(replacementAlias));

        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        fixture.Remote.Delete("shared.txt");
        var recoveryAlias = Path.Combine(Path.GetDirectoryName(fixture.LocalRoot)!, "recovery-alias.bin");
        var recoveryRejected = false;
        SyncLocalRecovery.RecoveryWriteIntervalForTest = recoveryPath =>
        {
            recoveryRejected = !NativeTestFiles.TryCreateHardLinkFromChild(recoveryAlias, recoveryPath);
        };
        try
        {
            var archive = await fixture.RunAsync();
            Equal(1, archive.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
        }
        finally { SyncLocalRecovery.RecoveryWriteIntervalForTest = null; }
        Equal(true, recoveryRejected);
        True(!File.Exists(recoveryAlias));
    });
}

#pragma warning disable CS8321
static Task WritableObjectCrashRecovery()
{
    return WithTempRoot(async root =>
    {
        var payload = CrashPayload();
        var targetPath = Path.Combine(root, CrashTargetName());
        foreach (var mode in new[] { "stage", "backup", "recovery" })
        {
            NativeTestFiles.RunAclCrashChild(mode, root);
            var path = CrashObjectPath(root, mode);
            RejectCrashObjectAlias(root, path, mode);
            True(SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(path, path) is not null);
            VerifyRecoveredObjectCanBeReadWrittenDeleted(path, payload);
            True(!File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(path)));
        }

        NativeTestFiles.RunAclCrashChild("promoted", root);
        RejectCrashObjectAlias(root, targetPath, "promoted");
        var promotedOwnerPath = CrashObjectPath(root, "promoted");
        True(SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(promotedOwnerPath, targetPath) is not null);
        VerifyRecoveredObjectCanBeReadWrittenDeleted(targetPath, payload);
        True(!File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(promotedOwnerPath)));
        await Task.CompletedTask;
    });
}

static Task NearCeilingCrashRecovery()
{
    return WithSyncFixture(async fixture =>
    {
        for (var index = 0; index < TestScale.StressEntryCount - 1; index++)
        {
            var name = $"near-ceiling-{index:D3}.txt";
            Write(fixture.LocalRoot, name, string.Empty);
            fixture.Remote.Put(name, string.Empty);
        }
        await fixture.RunAsync();

        var payload = CrashPayload();
        fixture.Remote.Put(CrashTargetName(), Encoding.UTF8.GetString(payload));
        var controlRoot = fixture.Pair.ControlNamespace!.Local.RootPath;
        NativeTestFiles.RunAclCrashChild("stage", controlRoot);
        var stagePath = CrashObjectPath(controlRoot, "stage");
        RejectCrashObjectAlias(controlRoot, stagePath, "near-ceiling-stage");
        Equal(TestScale.StressEntryCount - 1,
            Directory.EnumerateFileSystemEntries(fixture.LocalRoot).Count());
        True(SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath) is not null);
        using (var stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath)) stage.DeleteOnClose();

        var recovered = await fixture.RunAsync();
        Equal(1, recovered.Downloaded);
        var targetPath = Path.Combine(fixture.LocalRoot, CrashTargetName());
        True(File.ReadAllBytes(targetPath).SequenceEqual(payload));
        using (var writable = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Equal(payload.Length, writable.Length);
        True(!File.Exists(stagePath));
        True(!File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(stagePath)));
        Equal(TestScale.StressEntryCount,
            Directory.EnumerateFileSystemEntries(fixture.LocalRoot).Count());

        var ordinary = await fixture.RunAsync();
        Equal(0, ordinary.Blocked);
        Equal(TestScale.StressEntryCount, ordinary.Identical);
    });
}

static Task PrivateTransactionPreparedRecovery()
{
    return WithTempRoot(async root =>
    {
        var target = Path.Combine(root, "prepared.txt");
        File.WriteAllText(target, "baseline");
        var sourceHash = await HashBytesAsync(Bytes("replacement"));
        var baselineHash = await HashBytesAsync(Bytes("baseline"));
        using var rootLease = PilotDirectoryLease.Open(root);
        var names = SyncLocalTransfer.CreateArtifactNames(replacing: true);
        var intent = new SyncTransferIntent(Guid.NewGuid().ToString("N"), "pair-private", "prepared.txt",
            SyncDirection.Download, sourceHash, baselineHash, null, names.StageFileName, names.BackupFileName,
            DateTimeOffset.UtcNow, CreationPolicyRevision: 1);
        intent = SyncLocalTransactionDirectory.Prepare(rootLease, intent);
        var transaction = Path.Combine(root, intent.TransactionDirectoryName!);
        var unknownStage = Path.Combine(transaction, names.StageFileName);
        File.WriteAllText(unknownStage, "replacement");
        var remote = new PilotRemoteEntry("prepared.txt", PilotEntryKind.File, "id:prepared", "rev:prepared",
            Bytes("replacement").Length, sourceHash);
        await ThrowsAsync<IOException>(() => SyncLocalTransfer.RecoverInterruptedDownloadAsync(remote, rootLease, intent,
            static (_, _) => Task.CompletedTask, CancellationToken.None));
        Equal("replacement", File.ReadAllText(unknownStage));

        var empty = new SyncTransferIntent(Guid.NewGuid().ToString("N"), "pair-private", "empty.txt",
            SyncDirection.Download, sourceHash, null, null, names.StageFileName, null, DateTimeOffset.UtcNow,
            CreationPolicyRevision: 1);
        empty = SyncLocalTransactionDirectory.Prepare(rootLease, empty);
        var emptyRecovery = await SyncLocalTransfer.RecoverInterruptedDownloadAsync(
            remote with { RelativePath = "empty.txt" }, rootLease, empty, static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Equal(SyncLocalPendingDisposition.NotApplied, emptyRecovery.Disposition);
        Equal(false, emptyRecovery.Intent.ArtifactsBound);
        SyncLocalTransfer.FinalizeArtifacts(rootLease,
            emptyRecovery.Intent with { Phase = SyncIntentPhase.CommittedCleanupPending });
    });
}

static Task PrivateTransactionIdentityRecovery()
{
    return WithTempRoot(async root =>
    {
        var sourceHash = await HashBytesAsync(Bytes("replacement"));
        var baselineHash = await HashBytesAsync(Bytes("baseline"));
        using var rootLease = PilotDirectoryLease.Open(root);
        var names = SyncLocalTransfer.CreateArtifactNames(replacing: true);
        var intent = new SyncTransferIntent(Guid.NewGuid().ToString("N"), "pair-private", "restored.txt",
            SyncDirection.Download, sourceHash, baselineHash, null, names.StageFileName, names.BackupFileName,
            DateTimeOffset.UtcNow, CreationPolicyRevision: 1);
        intent = SyncLocalTransactionDirectory.Prepare(rootLease, intent);
        var transaction = Path.Combine(root, intent.TransactionDirectoryName!);
        var backupPath = Path.Combine(transaction, names.BackupFileName!);
        File.WriteAllText(backupPath, "baseline");
        using (var heldBackup = SyncLocalFileAuthority.OpenExistingExclusive(backupPath))
        {
            var identity = heldBackup.VerifySingleLink();
            intent = intent with
            {
                BackupArtifactId = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}",
                ArtifactsBound = true
            };
        }
        intent = await SyncLocalTransfer.BindPreparedArtifactsForRecoveryAsync(rootLease, intent,
            CancellationToken.None);
        File.Move(backupPath, Path.Combine(root, "restored.txt"));
        var remote = new PilotRemoteEntry("restored.txt", PilotEntryKind.File, "id:restored", "rev:restored",
            Bytes("replacement").Length, sourceHash);
        var restored = await SyncLocalTransfer.RecoverInterruptedDownloadAsync(remote, rootLease, intent,
            static (_, _) => Task.CompletedTask, CancellationToken.None);
        Equal(SyncLocalPendingDisposition.NotApplied, restored.Disposition);
        SyncLocalTransfer.FinalizeArtifacts(rootLease,
            restored.Intent with { Phase = SyncIntentPhase.CommittedCleanupPending });

        var substituted = new SyncTransferIntent(Guid.NewGuid().ToString("N"), "pair-private", "blocked.txt",
            SyncDirection.Download, sourceHash, null, null, names.StageFileName, null, DateTimeOffset.UtcNow,
            CreationPolicyRevision: 1);
        substituted = SyncLocalTransactionDirectory.Prepare(rootLease, substituted);
        var substitutedPath = Path.Combine(root, substituted.TransactionDirectoryName!, names.StageFileName);
        File.WriteAllText(substitutedPath, "attacker-substitution");
        await ThrowsAsync<IOException>(() => SyncLocalTransfer.RecoverInterruptedDownloadAsync(
            remote with { RelativePath = "blocked.txt" }, rootLease, substituted,
            static (_, _) => Task.CompletedTask, CancellationToken.None));
        Equal("attacker-substitution", File.ReadAllText(substitutedPath));
    });
}

static Task UnboundArtifactLookalikeIsCounted()
{
    return WithSyncFixture(async fixture =>
    {
        for (var index = 0; index < TestScale.StressEntryCount; index++)
            Write(fixture.LocalRoot, $"ordinary-{index:D3}.txt", string.Empty);
        var lookalike = Path.Combine(fixture.LocalRoot,
            ".syncshark-transfer-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.partial");
        File.WriteAllText(lookalike, "user-owned-lookalike");

        var result = await fixture.RunAsync();
        Equal(0, result.Blocked);
        Equal("user-owned-lookalike", File.ReadAllText(lookalike));
        Equal(0, fixture.Remote.UploadCount);
        var retained = fixture.StateStore.LoadPair(fixture.Pair.Id).Files;
        Equal(TestScale.StressEntryCount + 1, retained.Count);
        True(retained.Any(item => string.Equals(item.RelativePath,
            ".syncshark-transfer-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.partial",
            StringComparison.Ordinal)));
    });
}

static Task DropboxRecoverySpoolContract()
{
    return WithTempRoot(async root =>
    {
        var spoolRoot = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolRoot);
        DropboxPilotClient.RecoverySpoolRootForTest = spoolRoot;
        SyncRecoverySpoolAdmission.ResetForTest();
        try
        {
            var bytes = new byte[16 * 1024 * 1024];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
            await using var source = new MemoryStream(bytes, writable: false);
            var hash = await DropboxContentHash.ComputeAsync(source);
            var expected = new PilotRemoteEntry("large.bin", PilotEntryKind.File, "id:large-source",
                "rev:large-source", bytes.LongLength, hash);
            var recoveryPath =
                "/Apps/Sync Shark/Recovery/pair-identifier/0123456789abcdef0123456789abcdef";

            var orphan = Path.Combine(spoolRoot,
                "syncshark-recovery-spool-v1-orphan.tmp");
            await File.WriteAllTextAsync(orphan, "abandoned");
            NativeTestFiles.RunRecoverySpoolCrashChild(spoolRoot);
            var success = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot);
            using (var httpClient = new HttpClient(success))
            {
                var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var result = await client.CopyToRecoveryAsync(expected, recoveryPath);
                Equal(recoveryPath, result.RecoveryPath);
                Equal(hash, result.ContentHash);
            }
            True(success.SpoolObservedDuringUpload);
            True(!File.Exists(orphan));
            Equal(0, Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp").Length);
            Equal((0L, 0, 0), SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));

            var visible = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot);
            using (var httpClient = new HttpClient(visible))
            {
                var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var result = await client.CopyCreateOnlyAsync(expected, "large (Dropbox conflict).bin");
                Equal(hash, result.ContentHash);
            }
            True(visible.SpoolObservedDuringUpload);
            Equal(0, Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp").Length);

            foreach (var mode in new[]
                     {
                         "download-interrupted", "upload-rejected", "malformed-upload-ack",
                         "download-hash-mismatch", "renamed-before-download", "renamed-after-download",
                         "moved-before-download", "moved-after-download"
                     })
            {
                var handler = new DropboxRecoverySpoolHandler(bytes, hash, mode, spoolRoot);
                using var httpClient = new HttpClient(handler);
                var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                await ThrowsAsync<Exception>(() => client.CopyToRecoveryAsync(expected, recoveryPath));
                Equal(0, Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp").Length);
                Equal((0L, 0, 0), SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));
                if (mode is "renamed-before-download" or "moved-before-download")
                {
                    Equal(0, handler.DownloadCalls);
                    Equal(0, handler.UploadCalls);
                }
                if (mode is "renamed-after-download" or "moved-after-download")
                    Equal(0, handler.UploadCalls);
            }

            foreach (var mode in new[] { "renamed-after-download", "moved-after-download" })
            {
                var visibleChangedAfterDownload = new DropboxRecoverySpoolHandler(bytes, hash, mode, spoolRoot);
                using (var httpClient = new HttpClient(visibleChangedAfterDownload))
                {
                    var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
                        "id:pilot-folder");
                    await ThrowsAsync<Exception>(() =>
                        client.CopyCreateOnlyAsync(expected, "large (Dropbox conflict).bin"));
                }
                Equal(1, visibleChangedAfterDownload.DownloadCalls);
                Equal(0, visibleChangedAfterDownload.UploadCalls);
                Equal(0, Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp").Length);
            }

            // Two independent DropboxPilotClient instances share one volume admission ledger.
            // With capacity for exactly one source object, the first transfer retains its
            // reservation through upload while the second remains before its download boundary.
            SyncRecoverySpoolAdmission.CapacityBytesForTest = bytes.LongLength;
            SyncRecoverySpoolAdmission.WaitTimeoutForTest = TimeSpan.FromSeconds(5);
            var firstUploadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstUpload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstHandler = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot,
                firstUploadStarted, releaseFirstUpload);
            var cancelledHandler = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot);
            using (var firstHttp = new HttpClient(firstHandler))
            using (var cancelledHttp = new HttpClient(cancelledHandler))
            {
                var firstClient = new DropboxPilotClient(firstHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var cancelledClient = new DropboxPilotClient(cancelledHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var firstTask = firstClient.CopyToRecoveryAsync(expected, recoveryPath);
                await firstUploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancelled = new CancellationTokenSource();
                var cancelledTask = cancelledClient.CopyToRecoveryAsync(expected,
                    "/Apps/Sync Shark/Recovery/pair-identifier/1123456789abcdef0123456789abcdef", cancelled.Token);
                await WaitForSpoolWaiterAsync(spoolRoot);
                Equal(0, cancelledHandler.DownloadCalls);
                Equal((bytes.LongLength, 1, 1),
                    SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => cancelledTask);
                Equal((bytes.LongLength, 0, 1),
                    SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));
                releaseFirstUpload.TrySetResult(true);
                await firstTask;
            }
            Equal((0L, 0, 0), SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));

            // A bounded waiter times out without consuming capacity or disturbing the
            // admitted writer. Releasing the admitted writer still returns the ledger to zero.
            SyncRecoverySpoolAdmission.WaitTimeoutForTest = TimeSpan.FromMilliseconds(150);
            var timeoutUploadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseTimeoutUpload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeoutFirst = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot,
                timeoutUploadStarted, releaseTimeoutUpload);
            var timeoutSecond = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot);
            using (var firstHttp = new HttpClient(timeoutFirst))
            using (var secondHttp = new HttpClient(timeoutSecond))
            {
                var firstClient = new DropboxPilotClient(firstHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var secondClient = new DropboxPilotClient(secondHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var firstTask = firstClient.CopyToRecoveryAsync(expected, recoveryPath);
                await timeoutUploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ThrowsAsync<TimeoutException>(() => secondClient.CopyToRecoveryAsync(expected,
                    "/Apps/Sync Shark/Recovery/pair-identifier/3123456789abcdef0123456789abcdef"));
                Equal(0, timeoutSecond.DownloadCalls);
                Equal((bytes.LongLength, 0, 1),
                    SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));
                releaseTimeoutUpload.TrySetResult(true);
                await firstTask;
            }
            Equal((0L, 0, 0), SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));

            // FIFO progress: the second transfer cannot start downloading until the first
            // completes upload and releases its full-lifetime reservation, then proceeds.
            SyncRecoverySpoolAdmission.WaitTimeoutForTest = TimeSpan.FromSeconds(5);
            var progressUploadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProgressUpload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var progressFirst = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot,
                progressUploadStarted, releaseProgressUpload);
            var progressSecond = new DropboxRecoverySpoolHandler(bytes, hash, "success", spoolRoot);
            using (var firstHttp = new HttpClient(progressFirst))
            using (var secondHttp = new HttpClient(progressSecond))
            {
                var firstClient = new DropboxPilotClient(firstHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var secondClient = new DropboxPilotClient(secondHttp, new FixedTokenSource(), "/Pilot", Binding(),
                    "id:pilot-folder");
                var firstTask = firstClient.CopyToRecoveryAsync(expected, recoveryPath);
                await progressUploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var secondTask = secondClient.CopyToRecoveryAsync(expected,
                    "/Apps/Sync Shark/Recovery/pair-identifier/2123456789abcdef0123456789abcdef");
                await WaitForSpoolWaiterAsync(spoolRoot);
                Equal(0, progressSecond.DownloadCalls);
                releaseProgressUpload.TrySetResult(true);
                await Task.WhenAll(firstTask, secondTask);
            }
            Equal(1, progressSecond.DownloadCalls);
            Equal(1, progressSecond.UploadCalls);
            Equal((0L, 0, 0), SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(spoolRoot)!));
            Equal(0, Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp").Length);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "shared_recovery_spool_admission",
                processId = Environment.ProcessId,
                capacityBytes = bytes.LongLength,
                firstAndSecondCombinedBytes = bytes.LongLength * 2,
                maximumConcurrentSpools = 1,
                cancellationReleasedPhantomBytes = true,
                boundedTimeoutReleasedPhantomBytes = true,
                waiterEventuallyProceeded = true,
                processCrashRestartRecovered = true
            }));
        }
        finally
        {
            DropboxPilotClient.RecoverySpoolRootForTest = null;
            SyncRecoverySpoolAdmission.ResetForTest();
        }
    });
}

static async Task WaitForSpoolWaiterAsync(string spoolRoot)
{
    var volumeRoot = Path.GetPathRoot(spoolRoot)!;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (SyncRecoverySpoolAdmission.SnapshotForTest(volumeRoot).WaitingCount == 1) return;
        await Task.Delay(20);
    }
    throw new TimeoutException("A concurrent recovery transfer did not enter bounded spool admission.");
}

static async Task DropboxRangeDownloadWireContract()
{
    const long offset = 65_536;
    const long length = 4_096;
    var expected = DropboxRangeContractHandler.Expected;

    var handler = new DropboxRangeContractHandler();
    using (var httpClient = new HttpClient(handler))
    {
        var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await using var destination = new MemoryStream();
        var result = await client.DownloadRangeAsync(expected, offset, length, destination);
        Equal(length, destination.Length);
        Equal(offset, result.Offset);
        Equal(length, result.Length);
        Equal(expected.Size, result.TotalSize);
        Equal(2, handler.FileMetadataCalls);
        Equal(offset, handler.RangeFrom);
        Equal(offset + length - 1, handler.RangeTo);
        Equal(length, handler.ResponseBodyBytes);
        True(destination.ToArray().All(value => value == DropboxRangeContractHandler.RangeByte));
    }

    using (var httpClient = new HttpClient(new DropboxRangeContractHandler("pathless")))
    {
        var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await using var destination = new MemoryStream();
        var result = await client.DownloadRangeAsync(expected, offset, length, destination);
        Equal(length, result.Length);
        Equal(length, destination.Length);
    }

    using (var httpClient = new HttpClient(new DropboxRangeContractHandler("pathless-mismatch")))
    {
        var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await using var destination = new MemoryStream();
        await ThrowsAsync<IOException>(() => client.DownloadRangeAsync(expected, offset, length, destination));
    }

    foreach (var mode in new[] { "ignored-range", "truncated", "excess" })
    {
        using var httpClient = new HttpClient(new DropboxRangeContractHandler(mode));
        var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await using var destination = new MemoryStream();
        await ThrowsAsync<IOException>(() => client.DownloadRangeAsync(expected, offset, length, destination));
    }

    var movedHandler = new DropboxRangeContractHandler("moved-after-range");
    using (var httpClient = new HttpClient(movedHandler))
    {
        var client = new DropboxPilotClient(httpClient, new FixedTokenSource(), "/Pilot", Binding(),
            "id:pilot-folder");
        await using var destination = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(() =>
            client.DownloadRangeAsync(expected, offset, length, destination));
        Equal(0, movedHandler.EffectCalls);
    }
}

static Task CleanupPendingCrashMatrix()
{
    return WithTempRoot(async root =>
    {
        foreach (var nearCeiling in new[] { false, true })
        foreach (var scenario in new[] { "normal", "recovery", "keep-dropbox" })
        foreach (var boundary in new[] { "persisted", "verified" })
        {
            var caseRoot = Path.Combine(root, $"crash-{scenario}-{boundary}-{nearCeiling}");
            Directory.CreateDirectory(caseRoot);
            NativeTestFiles.RunCleanupCrashChild(scenario, boundary, nearCeiling, caseRoot);
            await WithCleanupFixture(caseRoot, initialize: false, nearCeiling, async fixture =>
            {
                var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
                Equal(SyncIntentPhase.CommittedCleanupPending, pending.Phase);
                if (scenario is "normal" or "recovery" && boundary == "persisted")
                {
                    True(pending.BackupArtifactId is not null);
                    True(File.Exists(Path.Combine(fixture.Pair.ControlNamespace!.Local.RootPath, pending.TransactionDirectoryName!,
                        pending.BackupFileName!)));
                }
                if (boundary == "verified") AssertCleanupArtifactsAbsent(fixture.Pair.ControlNamespace!.Local.RootPath, pending);

                var recovered = await fixture.RunAsync();
                Equal(1, recovered.Recovered);
                True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
                AssertCleanupArtifactsAbsent(fixture.Pair.ControlNamespace!.Local.RootPath, pending);
                AssertNoCleanupArtifactReachedRemote(fixture.Remote, pending);
                Equal(nearCeiling ? TestScale.StressEntryCount : 1,
                    Directory.EnumerateFiles(fixture.LocalRoot, "*", SearchOption.TopDirectoryOnly).Count());

                var ordinary = await fixture.RunAsync();
                Equal(0, ordinary.Blocked);
                AssertNoCleanupArtifactReachedRemote(fixture.Remote, pending);
            });
        }
    });
}

static Task CleanupArtifactIdentityMismatch()
{
    return WithTempRoot(async root =>
    {
        await WithCleanupFixture(root, initialize: true, nearCeiling: false, async fixture =>
        {
            await PrepareCleanupScenarioAsync(fixture, "normal", nearCeiling: false);
            BidirectionalSyncEngine.CleanupStatePersistedForTest = (_, _) =>
                throw new IOException("fixture retains the cleanup owner");
            try
            {
                var failed = await ExecuteCleanupScenarioAsync(fixture, "normal");
                Equal(1, failed.Blocked);
            }
            finally { BidirectionalSyncEngine.CleanupStatePersistedForTest = null; }

            var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
            True(pending.BackupArtifactId is not null);
            var backupPath = Path.Combine(fixture.Pair.ControlNamespace!.Local.RootPath, pending.TransactionDirectoryName!,
                pending.BackupFileName!);
            File.Delete(backupPath);
            File.WriteAllText(backupPath, "substituted-user-object");

            var blocked = await fixture.RunAsync();
            Equal(1, blocked.Blocked);
            Equal(SyncIntentPhase.CommittedCleanupPending,
                fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!.Phase);
            Equal("substituted-user-object", File.ReadAllText(backupPath));
            AssertNoCleanupArtifactReachedRemote(fixture.Remote, pending);
        });
    });
}

static Task CleanupFailureMatrix()
{
    return WithTempRoot(async root =>
    {
        foreach (var nearCeiling in new[] { false, true })
        foreach (var scenario in new[] { "normal", "recovery", "keep-dropbox" })
        foreach (var boundary in new[] { "persisted", "verified" })
        {
            var caseRoot = Path.Combine(root, $"failure-{scenario}-{boundary}-{nearCeiling}");
            Directory.CreateDirectory(caseRoot);
            await WithCleanupFixture(caseRoot, initialize: true, nearCeiling, async fixture =>
            {
                await PrepareCleanupScenarioAsync(fixture, scenario, nearCeiling);
                Action<SyncIntentKind, string> failure = (_, _) => throw new IOException("fixture cleanup failure");
                if (boundary == "persisted") BidirectionalSyncEngine.CleanupStatePersistedForTest = failure;
                else BidirectionalSyncEngine.CleanupArtifactsVerifiedForTest = failure;
                SyncRunResult failed;
                try { failed = await ExecuteCleanupScenarioAsync(fixture, scenario); }
                finally
                {
                    BidirectionalSyncEngine.CleanupStatePersistedForTest = null;
                    BidirectionalSyncEngine.CleanupArtifactsVerifiedForTest = null;
                }

                Equal(1, failed.Blocked);
                var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
                Equal(SyncIntentPhase.CommittedCleanupPending, pending.Phase);
                if (boundary == "verified") AssertCleanupArtifactsAbsent(fixture.Pair.ControlNamespace!.Local.RootPath, pending);

                var recovered = await fixture.RunAsync();
                Equal(1, recovered.Recovered);
                True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
                AssertCleanupArtifactsAbsent(fixture.Pair.ControlNamespace!.Local.RootPath, pending);
                AssertNoCleanupArtifactReachedRemote(fixture.Remote, pending);
                Equal(nearCeiling ? TestScale.StressEntryCount : 1,
                    Directory.EnumerateFiles(fixture.LocalRoot, "*", SearchOption.TopDirectoryOnly).Count());
            });
        }
    });
}

static async Task RunCleanupCrashProbeAsync(string scenario, string boundary, bool nearCeiling, string root)
{
    await WithCleanupFixture(root, initialize: true, nearCeiling, async fixture =>
    {
        await PrepareCleanupScenarioAsync(fixture, scenario, nearCeiling);
        Action<SyncIntentKind, string> terminate = (_, _) => NativeTestFiles.TerminateCurrentProcess(198);
        if (boundary == "persisted") BidirectionalSyncEngine.CleanupStatePersistedForTest = terminate;
        else if (boundary == "verified") BidirectionalSyncEngine.CleanupArtifactsVerifiedForTest = terminate;
        else throw new InvalidDataException("The cleanup crash boundary is not accepted.");
        await ExecuteCleanupScenarioAsync(fixture, scenario);
        throw new InvalidOperationException("The cleanup crash probe did not terminate at its selected boundary.");
    });
}

static async Task WithCleanupFixture(string root, bool initialize, bool nearCeiling, Func<SyncFixture, Task> action)
{
    var localRoot = Path.Combine(root, "local");
    Directory.CreateDirectory(localRoot);
    using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
    var remote = new FakeSyncRemoteStore();
    SyncPair pair;
    if (initialize)
    {
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        pair = SyncPair.FromPilot(new PilotSettings(localLease.Path, localLease.Identity.VolumeSerialNumber,
            localLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Cleanup fixture");
        pair = await BindFixtureControlAsync(pair, localLease,
            await pairStore.GetOrCreateInstallationIdAsync());
        var saved = await pairStore.UpsertAsync(pair);
        pair = saved.Pairs.Single();
    }
    else
    {
        pair = new SyncPairStore(appLock.StateAuthority).Load().Pairs.Single();
    }

    if (!initialize) PopulateCleanupRemote(remote, nearCeiling, replacement: true);
    await action(new SyncFixture(localRoot, pair, remote, new SyncRuntimeStateStore(appLock.StateAuthority)));
}

static async Task PrepareCleanupScenarioAsync(SyncFixture fixture, string scenario, bool nearCeiling)
{
    PopulateCleanupLocal(fixture.LocalRoot, nearCeiling);
    PopulateCleanupRemote(fixture.Remote, nearCeiling, replacement: false);
    await fixture.RunAsync();
    fixture.Remote.Put(CleanupTargetName(), CleanupRemoteBytes());

    if (scenario != "recovery") return;
    var targetPath = Path.Combine(fixture.LocalRoot, CleanupTargetName());
    PilotDirectoryIdentity targetIdentity;
    DateTime targetLastWriteUtc;
    using (var target = SyncLocalFileAuthority.OpenExistingExclusive(targetPath))
    {
        targetIdentity = target.VerifySingleLink();
        targetLastWriteUtc = File.GetLastWriteTimeUtc(targetPath);
    }
    File.Delete(targetPath);
    var artifacts = SyncLocalTransfer.CreateArtifactNames(replacing: true);
    var pending = new SyncTransferIntent(Guid.NewGuid().ToString("N"), fixture.Pair.Id, CleanupTargetName(),
        SyncDirection.Download, await HashBytesAsync(Bytes(CleanupRemoteBytes())),
        await HashBytesAsync(Bytes(CleanupBaselineBytes())), null, artifacts.StageFileName, artifacts.BackupFileName,
        DateTimeOffset.UtcNow, CreationPolicyRevision: fixture.Pair.PolicyRevision,
        ExpectedSourceId: "id:" + CleanupTargetName(), ExpectedSourceRevision: "rev:2",
        ExpectedSourceSize: Bytes(CleanupRemoteBytes()).Length,
        ExpectedDestinationVolumeSerialNumber: targetIdentity.VolumeSerialNumber,
        ExpectedDestinationFileId: targetIdentity.FileId,
        ExpectedSourceVolumeSerialNumber: targetIdentity.VolumeSerialNumber,
        ExpectedSourceFileId: targetIdentity.FileId, ExpectedSourceLastWriteUtc: targetLastWriteUtc);
    using (var controlLease = PilotDirectoryLease.OpenOwnedDirectory(
               fixture.Pair.ControlNamespace!.Local.RootPath,
               new PilotDirectoryIdentity(fixture.Pair.ControlNamespace.Local.RootVolumeSerialNumber,
                   fixture.Pair.ControlNamespace.Local.RootFileId)))
        pending = SyncLocalTransactionDirectory.Prepare(controlLease, pending);
    var transactionPath = Path.Combine(fixture.Pair.ControlNamespace!.Local.RootPath, pending.TransactionDirectoryName!);
    var stagePath = Path.Combine(transactionPath, artifacts.StageFileName);
    var backupPath = Path.Combine(transactionPath, artifacts.BackupFileName!);
    string stageIdentity;
    string backupIdentity;
    await using (var stage = SyncLocalFileAuthority.CreateNewFile(stagePath))
    {
        var writer = stage.CreateGuardedWriteStream();
        await writer.WriteAsync(Bytes(CleanupRemoteBytes()));
        await stage.Stream.FlushAsync();
        stage.Stream.Flush(true);
        var identity = stage.VerifySingleLink();
        stageIdentity = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
    }
    await using (var backup = SyncLocalFileAuthority.CreateNewFile(backupPath))
    {
        var writer = backup.CreateGuardedWriteStream();
        await writer.WriteAsync(Bytes(CleanupBaselineBytes()));
        await backup.Stream.FlushAsync();
        backup.Stream.Flush(true);
        var identity = backup.VerifySingleLink();
        backupIdentity = $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
    }
    pending = pending with
    {
        StageArtifactId = stageIdentity,
        BackupArtifactId = backupIdentity,
        ArtifactsBound = true
    };
    var state = fixture.StateStore.LoadPair(fixture.Pair.Id);
    await fixture.StateStore.SavePairAsync(state with { PendingTransfer = pending });
}

static Task<SyncRunResult> ExecuteCleanupScenarioAsync(SyncFixture fixture, string scenario)
{
    return scenario switch
    {
        "normal" or "recovery" => fixture.RunAsync(),
        "keep-dropbox" => ExecuteKeepDropboxCleanupAsync(fixture),
        _ => throw new InvalidDataException("The cleanup scenario is not accepted.")
    };
}

static async Task<SyncRunResult> ExecuteKeepDropboxCleanupAsync(SyncFixture fixture)
{
    Write(fixture.LocalRoot, CleanupTargetName(), CleanupLocalConflictBytes());
    return await fixture.ResolveAsync(CleanupTargetName(), SyncConflictResolution.KeepDropboxArchiveLocal);
}

static void PopulateCleanupLocal(string localRoot, bool nearCeiling)
{
    if (nearCeiling)
        for (var index = 0; index < TestScale.StressEntryCount - 1; index++)
            Write(localRoot, $"cleanup-{index:D3}.txt", string.Empty);
    Write(localRoot, CleanupTargetName(), CleanupBaselineBytes());
}

static void PopulateCleanupRemote(FakeSyncRemoteStore remote, bool nearCeiling, bool replacement)
{
    if (nearCeiling)
        for (var index = 0; index < TestScale.StressEntryCount - 1; index++)
            remote.Put($"cleanup-{index:D3}.txt", string.Empty);
    remote.Put(CleanupTargetName(), CleanupBaselineBytes());
    if (replacement) remote.Put(CleanupTargetName(), CleanupRemoteBytes());
}

static void AssertCleanupArtifactsAbsent(string localRoot, SyncTransferIntent pending)
{
    var transactionPath = Path.Combine(localRoot, pending.TransactionDirectoryName!);
    foreach (var name in new[] { pending.StageFileName, pending.BackupFileName }.Where(name => name is not null))
    {
        var path = Path.Combine(transactionPath, name!);
        True(!File.Exists(path));
        True(!Directory.Exists(path));
        True(!File.Exists(SyncLocalFileAuthority.RecoverySidecarPath(path)));
    }
    True(!Directory.Exists(transactionPath));
}

static void AssertNoCleanupArtifactReachedRemote(FakeSyncRemoteStore remote, SyncTransferIntent pending)
{
    Equal(0, remote.UploadCount);
    if (pending.StageFileName is not null) True(!remote.Exists(pending.StageFileName));
    if (pending.BackupFileName is not null) True(!remote.Exists(pending.BackupFileName));
}

static string CleanupTargetName() => "cleanup-target.txt";
static string CleanupBaselineBytes() => "cleanup-baseline";
static string CleanupRemoteBytes() => "cleanup-remote-choice";
static string CleanupLocalConflictBytes() => "cleanup-local-conflict";

static void RejectCrashObjectAlias(string root, string objectPath, string mode)
{
    var aliasPath = Path.Combine(Path.GetDirectoryName(root)!, $"crash-{mode}-alias.bin");
    Equal(false, NativeTestFiles.TryCreateHardLinkFromChild(aliasPath, objectPath));
    True(!File.Exists(aliasPath));
}

static void VerifyRecoveredObjectCanBeReadWrittenDeleted(string path, byte[] expected)
{
    True(File.ReadAllBytes(path).SequenceEqual(expected));
    using (var authority = SyncLocalFileAuthority.OpenExistingExclusive(path, FileAccess.ReadWrite))
    {
        authority.VerifySingleLink();
        authority.Stream.Position = authority.Stream.Length;
        authority.Stream.WriteByte(0x2a);
        authority.Stream.Flush(true);
        authority.DeleteOnClose();
    }
    True(!File.Exists(path));
}

static async Task<string> HashBytesAsync(byte[] bytes)
{
    await using var stream = new MemoryStream(bytes, writable: false);
    return await DropboxContentHash.ComputeAsync(stream);
}

static byte[] CrashPayload() => Encoding.UTF8.GetBytes("crash-proof-sync-shark-bytes");
static string CrashPairId() => "crashpair01";
static string CrashOperationId() => "dddddddddddddddddddddddddddddddd";
static string CrashTargetName() => "crash-restored.txt";
static string CrashStageName() => ".syncshark-transfer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.partial";
static string CrashUnusedStageName() => ".syncshark-transfer-cccccccccccccccccccccccccccccccc.partial";
static string CrashBackupName() => ".syncshark-transfer-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.backup";

static string CrashObjectPath(string root, string mode) => mode switch
{
    "stage" or "promoted" => Path.Combine(root, CrashStageName()),
    "backup" => Path.Combine(root, CrashBackupName()),
    "recovery" => Path.Combine(root, SyncLocalRecovery.RecoveryFolderPrefix + CrashPairId(),
        CrashOperationId() + ".recovery"),
    _ => throw new InvalidDataException("The ACL crash-probe mode is not accepted.")
};

static Task RecoverableDeletionPropagation()
{
    return WithSyncFixture(async original =>
    {
        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        Write(fixture.LocalRoot, "local-delete.txt", "same-local-delete");
        fixture.Remote.Put("local-delete.txt", "same-local-delete");
        Write(fixture.LocalRoot, "remote-delete.txt", "same-remote-delete");
        fixture.Remote.Put("remote-delete.txt", "same-remote-delete");
        await fixture.RunAsync();

        File.Delete(Path.Combine(fixture.LocalRoot, "local-delete.txt"));
        fixture.Remote.Delete("remote-delete.txt");
        var result = await fixture.RunAsync();
        if (result.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived) != 2)
            throw new InvalidOperationException("Deletion propagation decisions: " +
                string.Join(" | ", result.Decisions.Select(item => item.RelativePath + ":" + item.Kind + ":" + item.Reason)));
        Equal(2, result.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
        True(!fixture.Remote.Exists("local-delete.txt"));
        Equal(1, fixture.Remote.ArchiveCount);
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "remote-delete.txt")));
        var recoveryFolder = Directory.EnumerateDirectories(fixture.Pair.ControlNamespace!.Local.RootPath,
            ".syncshark-recovery-*").Single();
        Equal("same-remote-delete", File.ReadAllText(Directory.EnumerateFiles(recoveryFolder).Single()));
        var rerun = await fixture.RunAsync();
        Equal(0, rerun.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
    });
}

static Task RecoverableRenamePropagation()
{
    return WithSyncFixture(async original =>
    {
        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        Write(fixture.LocalRoot, "a-local-old.txt", "local-rename");
        fixture.Remote.Put("a-local-old.txt", "local-rename");
        Write(fixture.LocalRoot, "a-remote-old.txt", "remote-rename");
        fixture.Remote.Put("a-remote-old.txt", "remote-rename");
        await fixture.RunAsync();

        File.Move(Path.Combine(fixture.LocalRoot, "a-local-old.txt"), Path.Combine(fixture.LocalRoot, "z-local-new.txt"));
        var localRename = await fixture.RunAsync();
        Equal(1, localRename.Uploaded);
        Equal(1, localRename.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
        True(!fixture.Remote.Exists("a-local-old.txt"));
        Equal("local-rename", fixture.Remote.ReadText("z-local-new.txt"));

        fixture.Remote.Delete("a-remote-old.txt");
        fixture.Remote.Put("z-remote-new.txt", "remote-rename");
        var remoteRename = await fixture.RunAsync();
        Equal(1, remoteRename.Downloaded);
        Equal(1, remoteRename.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "a-remote-old.txt")));
        Equal("remote-rename", File.ReadAllText(Path.Combine(fixture.LocalRoot, "z-remote-new.txt")));
        var recoveryFolder = Directory.EnumerateDirectories(fixture.Pair.ControlNamespace!.Local.RootPath,
            ".syncshark-recovery-*").Single();
        True(Directory.EnumerateFiles(recoveryFolder).Any(path => File.ReadAllText(path) == "remote-rename"));
    });
}

static Task RecoverableDeletionArchiveUncertain()
{
    return WithSyncFixture(async original =>
    {
        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        Write(fixture.LocalRoot, "delete-me.txt", "unchanged-peer");
        fixture.Remote.Put("delete-me.txt", "unchanged-peer");
        await fixture.RunAsync();
        File.Delete(Path.Combine(fixture.LocalRoot, "delete-me.txt"));
        fixture.Remote.InterruptNextArchiveAfterEffect = true;

        var uncertain = await fixture.RunAsync();
        Equal(1, uncertain.Blocked);
        Equal(1, fixture.Remote.ArchiveCount);
        Equal(true, fixture.Remote.Exists("delete-me.txt"));
        Equal(SyncIntentKind.ArchiveRemoteAfterLocalDeletion,
            fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!.Kind);

        var reconciled = await fixture.RunAsync();
        if (reconciled.Recovered != 1)
            throw new InvalidOperationException("Remote deletion reconciliation: " + string.Join(" | ", reconciled.Decisions.Select(item => item.Kind + ":" + item.Reason)));
        Equal(1, fixture.Remote.ArchiveCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
    });
}

static Task RecoverySourceAbsenceIsNotProof()
{
    return WithSyncFixture(async original =>
    {
        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        Write(fixture.LocalRoot, "lost-without-recovery.txt", "unchanged-peer");
        fixture.Remote.Put("lost-without-recovery.txt", "unchanged-peer");
        await fixture.RunAsync();
        File.Delete(Path.Combine(fixture.LocalRoot, "lost-without-recovery.txt"));
        fixture.Remote.LoseNextArchiveRecovery = true;

        var uncertain = await fixture.RunAsync();
        Equal(1, uncertain.Blocked);
        Equal(true, fixture.Remote.Exists("lost-without-recovery.txt"));
        var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentKind.ArchiveRemoteAfterLocalDeletion, pending.Kind);

        fixture.Remote.Delete("lost-without-recovery.txt");

        var reconciled = await fixture.RunAsync();
        Equal(1, reconciled.Blocked);
        Equal(0, reconciled.Recovered);
        Equal(pending.Id, fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!.Id);
    });
}

static Task FixedLengthRecoveryName()
{
    return WithSyncFixture(async original =>
    {
        var fixture = original with { Pair = original.Pair with { PropagateDeletions = true } };
        var longName = new string('n', 236) + ".txt";
        Write(fixture.LocalRoot, longName, "near-component-headroom");
        fixture.Remote.Put(longName, "near-component-headroom");
        await fixture.RunAsync();
        fixture.Remote.Delete(longName);

        var result = await fixture.RunAsync();
        Equal(1, result.Decisions.Count(item => item.Kind == SyncDecisionKind.Archived));
        var recoveryFolder = Directory.EnumerateDirectories(fixture.Pair.ControlNamespace!.Local.RootPath,
            ".syncshark-recovery-*").Single();
        var recoveryMember = Path.GetFileName(Directory.EnumerateFiles(recoveryFolder).Single());
        Equal(41, recoveryMember.Length);
        True(recoveryMember.EndsWith(".recovery", StringComparison.Ordinal));
        True(!recoveryMember.Contains(longName, StringComparison.Ordinal));
    });
}

static Task RecoverableConflictResolution()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();

        Write(fixture.LocalRoot, "shared.txt", "choose-local");
        fixture.Remote.Put("shared.txt", "archive-remote");
        var keepLocal = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepLocalArchiveDropbox);
        if (keepLocal.Uploaded != 1)
            throw new InvalidOperationException("Keep-local decisions: " + string.Join(" | ",
                keepLocal.Decisions.Select(item => item.Kind + ":" + item.Reason)));
        Equal(1, keepLocal.Uploaded);
        Equal("choose-local", fixture.Remote.ReadText("shared.txt"));
        Equal(1, fixture.Remote.ArchiveCount);

        Write(fixture.LocalRoot, "shared.txt", "archive-local");
        fixture.Remote.Put("shared.txt", "choose-remote");
        var keepRemote = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal);
        Equal(1, keepRemote.Downloaded);
        Equal("choose-remote", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        var recoveryFolder = Directory.EnumerateDirectories(fixture.Pair.ControlNamespace!.Local.RootPath,
            ".syncshark-recovery-*").Single();
        True(Directory.EnumerateFiles(recoveryFolder).Any(path => File.ReadAllText(path) == "archive-local"));
    });
}

static Task LocalRecoveryResponseLoss()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "shared.txt", "archive-local");
        fixture.Remote.Put("shared.txt", "choose-dropbox");
        SyncLocalRecovery.SourcePreservedForTest = _ => throw new IOException("fixture lost the local archive acknowledgement");
        SyncRunResult interrupted;
        try { interrupted = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal); }
        finally { SyncLocalRecovery.SourcePreservedForTest = null; }

        Equal(1, interrupted.Blocked);
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "shared.txt")));
        var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentPhase.Prepared, pending.Phase);
        Equal(41, Path.GetFileName(pending.RecoveryPath!).Length);
        True(File.Exists(pending.RecoveryPath));

        var recovered = await fixture.RunAsync();
        if (recovered.Downloaded != 1)
            throw new InvalidOperationException("Local recovery response-loss replay: " + string.Join(" | ", recovered.Decisions.Select(item => item.Kind + ":" + item.Reason)));
        Equal("choose-dropbox", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
    });
}

static Task LocalRecoveryIdentityMismatch()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "shared.txt", "archive-local");
        fixture.Remote.Put("shared.txt", "choose-dropbox");
        fixture.Remote.InterruptNextDownload = true;
        var interrupted = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal);
        Equal(1, interrupted.Blocked);
        var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentPhase.ArchiveCompleted, pending.Phase);
        var persistedIdentity = pending.RecoveryFileId!;
        var recoveryPath = pending.RecoveryPath!;
        var replacementPath = recoveryPath + ".replacement";
        File.Copy(recoveryPath, replacementPath);
        File.Delete(recoveryPath);
        File.Move(replacementPath, recoveryPath);

        var beforeDownloads = fixture.Remote.DownloadCount;
        var blocked = await fixture.RunAsync();
        Equal(1, blocked.Blocked);
        Equal(beforeDownloads, fixture.Remote.DownloadCount);
        var retained = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentPhase.ArchiveCompleted, retained.Phase);
        Equal(persistedIdentity, retained.RecoveryFileId);
        True(File.Exists(recoveryPath));
        True(!File.Exists(Path.Combine(fixture.LocalRoot, "shared.txt")));
    });
}

static Task LocalRecoveryCollisionPreserved()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "shared.txt", "archive-local");
        fixture.Remote.Put("shared.txt", "choose-dropbox");
        SyncLocalRecovery.BeforeCreateForTest = path => File.WriteAllText(path, "archive-local");
        SyncRunResult collided;
        try { collided = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal); }
        finally { SyncLocalRecovery.BeforeCreateForTest = null; }

        Equal(1, collided.Blocked);
        var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentPhase.Prepared, pending.Phase);
        True(pending.RecoveryFileId is null);
        Equal("archive-local", File.ReadAllText(pending.RecoveryPath!));
        Equal("archive-local", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));

        var beforeDownloads = fixture.Remote.DownloadCount;
        var blocked = await fixture.RunAsync();
        Equal(1, blocked.Blocked);
        Equal(beforeDownloads, fixture.Remote.DownloadCount);
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is not null);
        Equal("archive-local", File.ReadAllText(pending.RecoveryPath!));
        Equal("archive-local", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
    });
}

static Task ConflictTargetIdentityMismatch()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();
        Write(fixture.LocalRoot, "shared.txt", "archive-local");
        fixture.Remote.Put("shared.txt", "choose-dropbox");
        SyncLocalTransfer.CommittedBeforeReturnForTest = _ =>
            throw new IOException("fixture retains the committed conflict target");
        SyncRunResult interrupted;
        try { interrupted = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal); }
        finally { SyncLocalTransfer.CommittedBeforeReturnForTest = null; }

        Equal(1, interrupted.Blocked);
        var pending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentPhase.ArchiveCompleted, pending.Phase);
        True(pending.StageArtifactId is not null);
        var target = Path.Combine(fixture.LocalRoot, "shared.txt");
        Equal("choose-dropbox", File.ReadAllText(target));

        var replacement = target + ".replacement";
        File.Copy(target, replacement);
        File.Delete(target);
        File.Move(replacement, target);
        var beforeDownloads = fixture.Remote.DownloadCount;

        var blocked = await fixture.RunAsync();
        Equal(1, blocked.Blocked);
        Equal(beforeDownloads, fixture.Remote.DownloadCount);
        Equal("choose-dropbox", File.ReadAllText(target));
        Equal(pending.StageArtifactId, fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!.StageArtifactId);
    });
}

static Task SameByteLocalIdentityReplacementConflicts()
{
    return WithSyncFixture(async fixture =>
    {
        const string name = "same-byte-identity.txt";
        Write(fixture.LocalRoot, name, "identity-bound-bytes");
        fixture.Remote.Put(name, "identity-bound-bytes");
        await fixture.RunAsync();
        var baseline = fixture.StateStore.LoadPair(fixture.Pair.Id).Files.Single(item => item.RelativePath == name);

        var target = Path.Combine(fixture.LocalRoot, name);
        var substitute = Path.Combine(fixture.LocalRoot, "identity-substitute.tmp");
        File.WriteAllText(substitute, "identity-bound-bytes");
        PilotDirectoryIdentity substituteIdentity;
        using (var held = SyncLocalFileAuthority.OpenExistingExclusive(substitute))
            substituteIdentity = held.VerifySingleLink();
        File.Delete(target);
        File.Move(substitute, target);
        True(baseline.LocalFileId != substituteIdentity.FileId ||
             baseline.LocalVolumeSerialNumber != substituteIdentity.VolumeSerialNumber);

        var result = await fixture.RunAsync();
        Equal(1, result.Conflicts);
        Equal(0, result.Uploaded);
        Equal(0, result.Downloaded);
        Equal("identity-bound-bytes", fixture.Remote.ReadText(name));
        var retained = fixture.StateStore.LoadPair(fixture.Pair.Id).Files.Single(item => item.RelativePath == name);
        Equal(baseline.LocalFileId, retained.LocalFileId);
        Equal(baseline.RemoteId, retained.RemoteId);
    });
}

static Task CompositeConflictRecovery()
{
    return WithSyncFixture(async fixture =>
    {
        Write(fixture.LocalRoot, "shared.txt", "baseline");
        fixture.Remote.Put("shared.txt", "baseline");
        await fixture.RunAsync();

        Write(fixture.LocalRoot, "shared.txt", "chosen-local");
        fixture.Remote.Put("shared.txt", "archive-this-remote");
        fixture.Remote.InterruptNextArchiveAfterEffect = true;
        var uncertainArchive = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepLocalArchiveDropbox);
        Equal(1, uncertainArchive.Blocked);
        Equal(1, fixture.Remote.ArchiveCount);
        Equal(true, fixture.Remote.Exists("shared.txt"));
        var keepLocalPending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentKind.ResolveKeepLocal, keepLocalPending.Kind);
        Equal(SyncIntentPhase.Prepared, keepLocalPending.Phase);

        var keepLocalRecovered = await fixture.RunAsync();
        Equal(1, keepLocalRecovered.Uploaded);
        Equal(1, fixture.Remote.ArchiveCount);
        Equal("chosen-local", fixture.Remote.ReadText("shared.txt"));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);

        Write(fixture.LocalRoot, "shared.txt", "archive-this-local");
        fixture.Remote.Put("shared.txt", "chosen-dropbox");
        fixture.Remote.InterruptNextDownload = true;
        var interruptedDownload = await fixture.ResolveAsync("shared.txt", SyncConflictResolution.KeepDropboxArchiveLocal);
        Equal(1, interruptedDownload.Blocked);
        Equal(false, File.Exists(Path.Combine(fixture.LocalRoot, "shared.txt")));
        var keepDropboxPending = fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer!;
        Equal(SyncIntentKind.ResolveKeepDropbox, keepDropboxPending.Kind);
        Equal(SyncIntentPhase.ArchiveCompleted, keepDropboxPending.Phase);

        var keepDropboxRecovered = await fixture.RunAsync();
        Equal(1, keepDropboxRecovered.Downloaded);
        Equal("chosen-dropbox", File.ReadAllText(Path.Combine(fixture.LocalRoot, "shared.txt")));
        var recoveryFolder = Directory.EnumerateDirectories(fixture.Pair.ControlNamespace!.Local.RootPath,
            ".syncshark-recovery-*").Single();
        True(Directory.EnumerateFiles(recoveryFolder).Any(path => File.ReadAllText(path) == "archive-this-local"));
        True(fixture.StateStore.LoadPair(fixture.Pair.Id).PendingTransfer is null);
    });
}

static Task OtherProviderPlaceholderIsolation()
{
    return WithSyncFixture(async fixture =>
    {
        var path = Path.Combine(fixture.LocalRoot, "placeholder.txt");
        Write(fixture.LocalRoot, "placeholder.txt", "local-placeholder-bytes");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        fixture.Remote.Put("placeholder.txt", "remote-bytes");
        try
        {
            var result = await fixture.RunAsync();
            Equal(1, result.OnlineOnly);
            Equal("local-placeholder-bytes", File.ReadAllText(path));
            Equal("remote-bytes", fixture.Remote.ReadText("placeholder.txt"));
            True(!fixture.StateStore.LoadPair(fixture.Pair.Id).Files.Any(item => item.RelativePath == "placeholder.txt"));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    });
}

static PilotSettings Settings(string root)
{
    using var lease = PilotDirectoryLease.Open(root);
    return new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Pilot", "id:pilot-folder");
}

static DropboxConnectionBinding Binding() => new(
    DropboxProviderConfiguration.RegisteredClientId,
    "account-id",
    "root-namespace",
    "home-namespace",
    DropboxProviderConfiguration.ContentAccess,
    DropboxProviderConfiguration.RequiredScopes);

static string CrossPairAbruptRecoveryEquivalenceKey(string direction)
{
    var canonical = string.Join("|", "CrossPairMove", direction, "EffectIssuing", "attempts=1",
        "terminal=Completed", "additional-effects=0", "recovery-artifacts=0");
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

static async Task WithSyncFixture(Func<SyncFixture, Task> action)
{
    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pairStore = new SyncPairStore(appLock.StateAuthority);
        var pair = SyncPair.FromPilot(new PilotSettings(localLease.Path, localLease.Identity.VolumeSerialNumber,
            localLease.Identity.FileId, "/Pilot", "id:pilot-folder"), "Fixture pair");
        var remote = new FakeSyncRemoteStore();
        pair = await BindFixtureControlAsync(pair, localLease,
            await pairStore.GetOrCreateInstallationIdAsync());
        await pairStore.UpsertAsync(pair);
        var fixture = new SyncFixture(localRoot, pair, remote, new SyncRuntimeStateStore(appLock.StateAuthority));
        await action(fixture);
    });
}

static Task<SyncPair> BindFixtureControlAsync(SyncPair pair, PilotDirectoryLease localLease,
    string installationId)
{
    var namespaceId = pair.Id;
    var local = SyncControlNamespace.BindLocalForTest(localLease, installationId, namespaceId);
    var root = SyncControlNamespace.RemoteContainerPath + "/" + namespaceId;
    var remoteBinding = new SyncRemoteControlNamespaceBinding(root, "id:control-" + namespaceId,
        root + "/.syncshark-owner.json", "id:marker-" + namespaceId, "rev:marker-1",
        DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installationId, namespaceId)));
    return Task.FromResult(pair with
    {
        ControlNamespace = new SyncControlNamespaceBinding(installationId, namespaceId, local, remoteBinding)
    });
}

static async Task PairSelectionDefaultContract()
{
    var subsetRules = new[]
    {
        new SyncSelectionRule("Work/Invoices", true, "id:invoices"),
        new SyncSelectionRule("Work/Invoices/Private", false, "id:private")
    };
    var subset = SyncSelectionPolicy.Compile(subsetRules, defaultIncluded: false);
    True(subset.IsIncluded("Work/Invoices/current.pdf"));
    True(!subset.IsIncluded("Work/Invoices/Private/secret.pdf"));
    True(!subset.IsIncluded("Work/Photos/future.jpg"));
    True(!subset.IsIncluded("future-root-file.txt"));
    True(subset.ShouldTraverseDirectory("Work"));
    True(!subset.ShouldTraverseDirectory("Unselected"));

    var parentIncluded = SyncSelectionPolicy.Compile(
        SyncSelectionPolicy.SetFolder(subsetRules, "Work", included: true), defaultIncluded: false);
    True(parentIncluded.IsIncluded("Work/Photos/future.jpg"));
    var parentExcluded = SyncSelectionPolicy.Compile(
        SyncSelectionPolicy.SetFolder(subsetRules, "Work", included: false), defaultIncluded: false);
    True(!parentExcluded.IsIncluded("Work/Invoices/current.pdf"));

    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        using var local = PilotDirectoryLease.Open(localRoot);
        var legacyTemplate = new SyncPair("selectiondefault0001", "Legacy pair", local.Path,
            local.Identity.VolumeSerialNumber, local.Identity.FileId, "/Legacy", "id:legacy-root");
        var legacyNode = JsonNode.Parse(JsonSerializer.Serialize(legacyTemplate))!.AsObject();
        legacyNode.Remove(nameof(SyncPair.SelectionDefaultIncluded));
        var legacy = JsonSerializer.Deserialize<SyncPair>(legacyNode.ToJsonString()) ??
                     throw new InvalidDataException("Legacy pair did not deserialize.");
        True(legacy.SelectionDefaultIncluded,
            "A legacy pair without the new field must remain include-by-default.");

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncPairStore(appLock.StateAuthority);
        await store.SaveAsync(new SyncPairSet([legacyTemplate], legacyTemplate.Id));
        var selected = legacyTemplate with
        {
            SelectionRules = subsetRules,
            SelectionDefaultIncluded = false
        };
        var updated = await store.UpsertAsync(selected, replacingPairId: legacyTemplate.Id);
        var retained = updated.Pairs.Single();
        True(!retained.SelectionDefaultIncluded);
        Equal(2L, retained.PolicyRevision);
        var reloaded = store.Load().Pairs.Single();
        True(!reloaded.SelectionDefaultIncluded);
        True(SyncSelectionPolicy.Compile(reloaded.SelectionRules, reloaded.SelectionDefaultIncluded)
            .IsIncluded("Work/Invoices/current.pdf"));
        True(!SyncSelectionPolicy.Compile(reloaded.SelectionRules, reloaded.SelectionDefaultIncluded)
            .IsIncluded("Work/Photos/future.jpg"));
    });

    await WithTempRoot(async root =>
    {
        var localRoot = Path.Combine(root, "local-catalog");
        Directory.CreateDirectory(Path.Combine(localRoot, "Invoices"));
        Directory.CreateDirectory(Path.Combine(localRoot, "Shared"));
        Write(localRoot, "Invoices/local.pdf", "local only");
        Write(localRoot, "Shared/local.txt", "present on both sides");
        using var local = PilotDirectoryLease.Open(localRoot);
        var pair = new SyncPair("selectioncatalog001", "Selection catalog", local.Path,
            local.Identity.VolumeSerialNumber, local.Identity.FileId, "/Catalog", "id:catalog-root",
            SelectionRules: [], SelectionDefaultIncluded: false);
        var remote = new FakeSyncRemoteStore();
        remote.PutDirectory("Shared");
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "catalog-state", "authority.db"));
        await authority.InitializeAsync("installation-selection-catalog", "profile-selection-catalog",
            "selection-catalog-test", "{}");
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-selection-catalog-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { local.Identity.VolumeSerialNumber, local.Identity.FileId }), local.Path);
        await authority.RegisterRootBindingAsync("root-selection-catalog-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:catalog-root\"}", pair.DropboxRoot);
        var observed = await new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-selection-catalog-local", "root-selection-catalog-dropbox").ObserveAsync("manual");
        Equal(0, observed.LocalEntries);
        Equal(0, observed.LocalFilesHashed);
        var page = await authority.LoadFolderSelectionPageAsync(pair.Id, 50, 0);
        True(page is not null);
        var invoices = page!.Folders.Single(item => item.RelativePath == "Invoices");
        var shared = page.Folders.Single(item => item.RelativePath == "Shared");
        Equal(SyncFolderPresence.LocalOnly, invoices.Presence);
        Equal(SyncFolderPresence.Mixed, shared.Presence);
        True(invoices.IsChecked == false && shared.IsChecked == false);
        True(!string.IsNullOrWhiteSpace(invoices.LocalStableFolderIdentity));
        True(!string.IsNullOrWhiteSpace(shared.DropboxStableFolderIdentity));
    });
}

[SupportedOSPlatform("windows")]
static Task PairLifecycleUiContract()
{
    return WithTempRoot(async root =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var localA = Path.Combine(root, "local-a");
        var localB = Path.Combine(root, "local-b");
        Directory.CreateDirectory(localA);
        Directory.CreateDirectory(localB);
        using var leaseA = PilotDirectoryLease.Open(localA);
        using var leaseB = PilotDirectoryLease.Open(localB);
        var pairA = SyncPair.FromPilot(new PilotSettings(leaseA.Path, leaseA.Identity.VolumeSerialNumber,
            leaseA.Identity.FileId, "/A", "id:a"), "Alpha") with { RecoveryRetentionDays = 90 };
        var pairB = SyncPair.FromPilot(new PilotSettings(leaseB.Path, leaseB.Identity.VolumeSerialNumber,
            leaseB.Identity.FileId, "/B", "id:b"), "Copy of Alpha") with { Enabled = false };

        var legacyNode = JsonNode.Parse(JsonSerializer.Serialize(pairB))!.AsObject();
        legacyNode.Remove(nameof(SyncPair.RecoveryRetentionDays));
        var legacyPair = JsonSerializer.Deserialize<SyncPair>(legacyNode.ToJsonString())!;
        True(legacyPair.RecoveryRetentionDays == 30,
            "A saved pair without the new retention field must keep the prior 30-day authority policy.");

        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncPairStore(appLock.StateAuthority);
        await store.SaveAsync(new SyncPairSet([pairA, pairB], pairA.Id));
        var installationId = await store.GetOrCreateInstallationIdAsync();
        var afterRemoval = await store.RemoveAsync(pairA.Id);
        Equal(pairB.Id, afterRemoval.SelectedPairId);
        Equal(1, afterRemoval.Pairs.Count);
        True(afterRemoval.Pairs.All(item => item.Id != pairA.Id));
        True(afterRemoval.Pairs.Single() is { Name: "Copy of Alpha", Enabled: false });

        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync(installationId, "profile-pair-lifecycle",
            "pair-lifecycle-test", "{}");
        await authority.RegisterPairAsync(pairA.Id, pairA.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", true, false, 90));
        await authority.RetainPairDefinitionAsync(pairA, select: true);
        const string localBindingA = "root-pair-lifecycle-a-local";
        const string dropboxBindingA = "root-pair-lifecycle-a-dropbox";
        var localIdentityA = JsonSerializer.Serialize(new
        {
            volumeSerial = pairA.LocalVolumeSerialNumber.ToString("x8"),
            fileId = pairA.LocalRootFileId.ToString("x16")
        });
        const string dropboxIdentityA = "{\"folderId\":\"id:a\"}";
        await authority.RegisterRootBindingAsync(localBindingA, pairA.Id, "local", 1,
            localIdentityA, pairA.LocalRoot);
        await authority.RegisterRootBindingAsync(dropboxBindingA, pairA.Id, "dropbox", 1,
            dropboxIdentityA, pairA.DropboxRoot);
        await authority.RegisterPairAsync(pairB.Id, pairB.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", true, false, 30));
        await authority.RetainPairDefinitionAsync(pairB, select: false);
        var active = await authority.RetirePairDefinitionAsync(pairA.Id);
        Equal(pairB.Id, active.SelectedPairId);
        True(active.Pairs.All(item => item.Id != pairA.Id));

        await using var connection = new SqliteConnection("Data Source=" + authority.DatabasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,pair_json IS NOT NULL FROM sync_pair WHERE pair_id=$id;";
        command.Parameters.AddWithValue("$id", pairA.Id);
        await using var reader = await command.ExecuteReaderAsync();
        True(await reader.ReadAsync());
        Equal("retired", reader.GetString(0));
        True(reader.GetInt64(1) == 1,
            "Retirement must retain the pair identity needed by Recovery and history.");
        await reader.DisposeAsync();
        command.CommandText = "SELECT count(*) FROM root_binding WHERE pair_id=$id AND state='out_of_scope';";
        Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        await authority.RegisterPairAsync(pairA.Id, pairA.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", true, false, 90));
        await authority.RegisterRootBindingAsync(localBindingA, pairA.Id, "local", 1,
            localIdentityA, pairA.LocalRoot);
        await authority.RegisterRootBindingAsync(dropboxBindingA, pairA.Id, "dropbox", 1,
            dropboxIdentityA, pairA.DropboxRoot);
        await authority.RetainPairDefinitionAsync(pairA, select: true);
        command.CommandText = "SELECT state FROM sync_pair WHERE pair_id=$id;";
        Equal("discovery_required", Convert.ToString(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT count(*) FROM root_binding WHERE pair_id=$id AND state='active';";
        Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        True((await authority.LoadPairSetAsync()).Pairs.Any(item => item.Id == pairA.Id),
            "A deterministically identical pair must be recreatable after its retained deletion history is retired.");

        var localD = Path.Combine(root, "local-d");
        Directory.CreateDirectory(localD);
        using var leaseD = PilotDirectoryLease.Open(localD);
        var pairD = SyncPair.FromPilot(new PilotSettings(leaseD.Path, leaseD.Identity.VolumeSerialNumber,
            leaseD.Identity.FileId, "/D", "id:d"), "Rebound Alpha");
        await authority.RegisterPairAsync(pairD.Id, pairD.PolicyRevision,
            new SyncAuthorityPairConfiguration("disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", true, false, 90));
        const string localBindingD = "root-pair-lifecycle-d-local";
        const string dropboxBindingD = "root-pair-lifecycle-d-dropbox";
        await authority.RegisterRootBindingAsync(localBindingD, pairD.Id, "local", 1,
            JsonSerializer.Serialize(new
            {
                volumeSerial = pairD.LocalVolumeSerialNumber.ToString("x8"),
                fileId = pairD.LocalRootFileId.ToString("x16")
            }), pairD.LocalRoot);
        await authority.RegisterRootBindingAsync(dropboxBindingD, pairD.Id, "dropbox", 1,
            "{\"folderId\":\"id:d\"}", pairD.DropboxRoot);
        var rebound = await authority.ReplacePairDefinitionAsync(pairA.Id, pairD);
        Equal(pairD.Id, rebound.SelectedPairId);
        True(rebound.Pairs.Any(item => item.Id == pairD.Id));
        True(rebound.Pairs.All(item => item.Id != pairA.Id));
        command.CommandText = "SELECT state FROM sync_pair WHERE pair_id=$id;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", pairA.Id);
        Equal("retired", Convert.ToString(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT count(*) FROM root_binding WHERE pair_id=$id AND state='out_of_scope';";
        Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        var providerConnection = Binding().Normalize();
        var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
        await connectionStore.SaveNewAsync(providerConnection);
        var localC = Path.Combine(root, "local-c");
        Directory.CreateDirectory(localC);
        var remote = new FakeSyncRemoteStore();
        using var bindingHttp = new HttpClient(new DropboxRecoveryBrowserContractHandler());
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
            bindingHttp, store,
            new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore,
            new MemoryDropboxRefreshTokenStore("retained-refresh-token"), authority, (_, _) => remote);
        var bindingRequest = new SyncPairBindingRequest(null, "Copy of Alpha (2)", localC,
            new DropboxFolderEntry("Recovery Contract", "/Recovery Contract", "id:recovery-contract"),
            new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery",
                "id:recovery-root"),
            Enabled: false, Priority: 0, PropagateDeletions: true, FilesOnDemandEnabled: false,
            SelectionRules: [], SelectionDefaultIncluded: true, RecoveryRetentionDays: 90);
        var created = await host.BindPairAsync(bindingRequest);
        True(created.Pair is { Name: "Copy of Alpha (2)", Enabled: false });
        var enabled = await host.SetPairEnabledAsync(created.Pair.Id, true);
        True(enabled.Pairs.Single(item => item.Id == created.Pair.Id).Enabled);
        var paused = await host.SetPairEnabledAsync(created.Pair.Id, false);
        True(!paused.Pairs.Single(item => item.Id == created.Pair.Id).Enabled);
        var deleted = await host.RetirePairAsync(created.Pair.Id);
        True(deleted.Pairs.All(item => item.Id != created.Pair.Id));
        var recreated = await host.BindPairAsync(bindingRequest);
        Equal(created.Pair.Id, recreated.Pair.Id);
        True(recreated.Pair is { Enabled: false, InitialReviewApproved: false });
    });
}

static async Task WithTempRoot(Func<string, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), "sync-shark-pilot-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { await action(root); }
    finally
    {
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, true);
                break;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
        }
    }
}

static void Write(string root, string relative, string value)
{
    var path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, value);
}

static void CreateDirectoryJunction(string junctionPath, string targetPath)
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

static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

static void True(bool value, string? message = null)
{
    if (!value) throw new InvalidOperationException(message ?? "Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

static void Contains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException("Missing expected text: " + expected);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class FakeRemoteStore : IPilotRemoteStore
{
    private readonly bool _throwUncertain;
    public Dictionary<string, byte[]> Files { get; }
    public int UploadCount { get; private set; }
    public int VerifyCount { get; private set; }

    public FakeRemoteStore(IEnumerable<KeyValuePair<string, byte[]>> files, bool throwUncertain = false)
    {
        Files = files.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _throwUncertain = throwUncertain;
    }

    public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default)
    {
        VerifyCount++;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PilotRemoteEntry>();
        foreach (var item in Files)
            result.Add(new PilotRemoteEntry(item.Key, PilotEntryKind.File, "id:" + item.Key, "rev:" + item.Key,
                item.Value.Length, await HashBytesAsync(item.Value)));
        return result;
    }

    public async Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source, string expectedContentHash, CancellationToken cancellationToken = default)
    {
        if (_throwUncertain) throw new DropboxEffectUncertainException("fixture interruption");
        if (Files.ContainsKey(relativePath)) throw new DropboxApiException(HttpStatusCode.Conflict, "create-only conflict");
        await using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (!string.Equals(expectedContentHash, await HashBytesAsync(bytes), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fake upload hash mismatch.");
        Files.Add(relativePath, bytes);
        UploadCount++;
        return new PilotUploadResult("id:" + relativePath, "rev:" + relativePath, bytes.Length, expectedContentHash);
    }

    private static async Task<string> HashBytesAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await DropboxContentHash.ComputeAsync(stream);
    }
}

sealed record SyncFixture(string LocalRoot, SyncPair Pair, FakeSyncRemoteStore Remote, SyncRuntimeStateStore StateStore)
{
    public async Task<SyncRunResult> RunAsync()
    {
        var result = await new BidirectionalSyncEngine(Pair, Remote, StateStore, new PilotRunControl()).RunAsync();
        var state = StateStore.LoadPair(Pair.Id);
        if (!state.BaselineEstablished && state.InitialObservationCount == 1)
            result = await new BidirectionalSyncEngine(Pair, Remote, StateStore, new PilotRunControl()).RunAsync();
        return result;
    }
    public Task<SyncRunResult> ResolveAsync(string path, SyncConflictResolution resolution) =>
        new BidirectionalSyncEngine(Pair, Remote, StateStore, new PilotRunControl()).ResolveConflictAsync(path, resolution);
}

sealed class FakeSyncRemoteStore : ISyncRemoteStore, ISyncRemoteRangeStore, ISyncRemoteChangeFeed, ISyncCrossRootRemoteMove
{
    public bool SuppressContentHashesForTest { get; set; }
    // Deterministic fixture only: DeleteAsync checks descendants synchronously
    // at its effect boundary. This is not a claim about Dropbox's API.
    public bool SupportsConditionalDirectoryDelete => true;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, int Revision, string Id)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SyncArchiveResult> _archive = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(long Sequence, string? Path, string Kind)> _changes = [];
    private readonly object _changeGate = new();
    private long _changeSequence;
    private int _uploadCount;
    public int UploadCount => Volatile.Read(ref _uploadCount);
    private int _downloadCount;
    public int DownloadCount => Volatile.Read(ref _downloadCount);
    public int ArchiveCount { get; private set; }
    public int VisibleCopyCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int MoveCount { get; private set; }
    private int _directoryCreateCount;
    public int DirectoryCreateCount => Volatile.Read(ref _directoryCreateCount);
    public int RangeDownloadCount { get; private set; }
    public long RangeDownloadBytes { get; private set; }
    public string CurrentCursor { get { lock (_changeGate) return _changeSequence.ToString(); } }
    public bool ResetNextCursor { get; set; }
    public bool CycleNextChangeCursorSequence { get; set; }
    public bool InterruptNextChangePoll { get; set; }
    public bool InterruptNextUploadAfterEffect { get; set; }
    public bool InterruptNextUploadBeforeEffect { get; set; }
    public bool InterruptNextDownload { get; set; }
    public bool InterruptNextArchiveAfterEffect { get; set; }
    public bool InterruptNextArchiveBeforeEffect { get; set; }
    public bool InterruptNextVisibleCopyAfterEffect { get; set; }
    public bool LoseNextArchiveRecovery { get; set; }
    public bool InterruptNextDeleteAfterEffect { get; set; }
    public bool InterruptNextMoveBeforeEffect { get; set; }
    public bool InterruptNextMoveAfterEffect { get; set; }
    public Func<CancellationToken, Task>? BeforeListEntriesForTest { get; set; }
    public Func<CancellationToken, Task>? BeforeDownloadForTest { get; set; }
    public Func<string, CancellationToken, Task>? BeforeReadEntryForTest { get; set; }
    public Func<string, Exception?>? UploadFailureForTest { get; set; }
    public Func<string, Exception?>? DownloadFailureForTest { get; set; }
    public Action? AfterListEntriesForTest { get; set; }
    // R595 opt-in: while this is enabled an exact-path or exact-identity lookup is answered
    // from the current in-memory inventory without enumerating the tree and without running
    // the list fault/race hooks, which is how Dropbox's files/get_metadata endpoint behaves.
    // The shared legacy listing fallback stays the default for every other consumer.
    public bool ExactPathMetadataForTest { get; set; }
    public int ListEntriesCount { get; private set; }
    public int ExactPathLookupCount { get; private set; }

    public void Put(string path, string value)
    {
        PutBytes(path, Encoding.UTF8.GetBytes(value));
    }

    // J3: an optional explicit positive revision lets a fixture inject a pre-applied destination revision
    // without changing any existing caller, whose default stays revision 1.
    public void PutWithId(string path, string value, string id, int revision = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        _files[path] = (Encoding.UTF8.GetBytes(value), revision, id);
        RecordChange(path, "file");
    }

    public void PutBytes(string path, byte[] bytes)
    {
        var nextRevision = _files.TryGetValue(path, out var current) ? current.Revision + 1 : 1;
        var id = _files.TryGetValue(path, out current) ? current.Id : "id:" + path;
        _files[path] = (bytes.ToArray(), nextRevision, id);
        RecordChange(path, "file");
    }

    public string ReadText(string path) => Encoding.UTF8.GetString(_files[path].Bytes);
    public bool Exists(string path) => _files.ContainsKey(path);
    public bool DirectoryExists(string path) => _directories.ContainsKey(path);
    public void PutDirectory(string path)
    {
        PutDirectoryWithId(path, "id:folder:" + path);
    }
    public void PutDirectoryWithId(string path, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_files.ContainsKey(path)) throw new IOException("Fake directory path is occupied by a file.");
        if (_directories.TryAdd(path, id)) RecordChange(path, "folder");
    }
    public void MoveDirectory(string fromPath, string toPath)
    {
        if (!_directories.TryGetValue(fromPath, out var identity))
            throw new IOException("Fake directory move source is absent.");
        if (_directories.ContainsKey(toPath) || _files.ContainsKey(toPath))
            throw new IOException("Fake directory move target exists.");
        var sourcePrefix = fromPath + "/";
        var destinationPrefix = toPath + "/";
        var directoryMembers = _directories.Where(item => item.Key.StartsWith(sourcePrefix,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var fileMembers = _files.Where(item => item.Key.StartsWith(sourcePrefix,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        _directories.TryRemove(fromPath, out _);
        foreach (var member in directoryMembers) _directories.TryRemove(member.Key, out _);
        foreach (var member in fileMembers) _files.TryRemove(member.Key, out _);
        _directories[toPath] = identity;
        foreach (var member in directoryMembers)
            _directories[destinationPrefix + member.Key[sourcePrefix.Length..]] = member.Value;
        foreach (var member in fileMembers)
            _files[destinationPrefix + member.Key[sourcePrefix.Length..]] = member.Value;
        RecordChange(fromPath, "deleted");
        RecordChange(toPath, "folder");
    }
    public void DeleteDirectory(string path)
    {
        if (_directories.TryRemove(path, out _)) RecordChange(path, "deleted");
    }
    public void Delete(string path)
    {
        if (_files.TryRemove(path, out _)) RecordChange(path, "deleted");
    }
    public void Move(string fromPath, string toPath, bool churnRevision = false)
    {
        var current = _files[fromPath];
        if (_files.TryGetValue(toPath, out var occupant) &&
            !string.Equals(occupant.Id, current.Id, StringComparison.Ordinal))
            throw new IOException("Fake move target exists.");
        _files.TryRemove(fromPath, out _);
        _files[toPath] = churnRevision
            ? (current.Bytes, checked(current.Revision + 1), current.Id)
            : current;
        RecordChange(fromPath, "deleted");
        RecordChange(toPath, "file");
    }

    public async Task<PilotRemoteEntry> MoveAsync(PilotRemoteEntry expected,
        string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (InterruptNextMoveBeforeEffect)
        {
            InterruptNextMoveBeforeEffect = false;
            throw new DropboxEffectUncertainException(
                "fixture lost the native-move response before applying the effect");
        }
        if (expected.Kind == PilotEntryKind.Directory)
        {
            if (!_directories.TryGetValue(expected.RelativePath, out var directoryId) ||
                !string.Equals(directoryId, expected.Id, StringComparison.Ordinal))
                throw new DropboxApiException(HttpStatusCode.Conflict, "native folder move conflict");
            var directories = _directories.Where(item => IsSameOrDescendant(item.Key, expected.RelativePath))
                .ToArray();
            var files = _files.Where(item => IsDescendant(item.Key, expected.RelativePath)).ToArray();
            var movingDirectories = directories.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var movingFiles = files.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetDirectories = directories.Select(item =>
                Rebase(item.Key, expected.RelativePath, destinationRelativePath)).ToArray();
            var targetFiles = files.Select(item =>
                Rebase(item.Key, expected.RelativePath, destinationRelativePath)).ToArray();
            if (targetDirectories.Any(path =>
                    _files.ContainsKey(path) && !movingFiles.Contains(path) ||
                    _directories.ContainsKey(path) && !movingDirectories.Contains(path)) ||
                targetFiles.Any(path =>
                    _files.ContainsKey(path) && !movingFiles.Contains(path) ||
                    _directories.ContainsKey(path) && !movingDirectories.Contains(path)))
                throw new DropboxApiException(HttpStatusCode.Conflict, "native folder move destination conflict");
            foreach (var item in directories) _directories.TryRemove(item.Key, out _);
            foreach (var item in files) _files.TryRemove(item.Key, out _);
            foreach (var item in directories)
                _directories[Rebase(item.Key, expected.RelativePath, destinationRelativePath)] = item.Value;
            foreach (var item in files)
                _files[Rebase(item.Key, expected.RelativePath, destinationRelativePath)] = item.Value;
            RecordChange(expected.RelativePath, "deleted");
            RecordChange(destinationRelativePath, "folder");
            MoveCount++;
            if (InterruptNextMoveAfterEffect)
            {
                InterruptNextMoveAfterEffect = false;
                throw new DropboxEffectUncertainException(
                    "fixture lost the native-folder-move response after applying the effect");
            }
            return new PilotRemoteEntry(destinationRelativePath, PilotEntryKind.Directory, directoryId,
                null, 0, null);
        }
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal) ||
            current.Bytes.Length != expected.Size ||
            !string.Equals(await HashAsync(current.Bytes), expected.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new DropboxApiException(HttpStatusCode.Conflict, "native move source conflict");
        if (_files.TryGetValue(destinationRelativePath, out var destinationOccupant) &&
            !string.Equals(destinationOccupant.Id, expected.Id, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "native move destination conflict");
        _files.TryRemove(expected.RelativePath, out _);
        _files[destinationRelativePath] = current;
        RecordChange(expected.RelativePath, "deleted");
        RecordChange(destinationRelativePath, "file");
        MoveCount++;
        if (InterruptNextMoveAfterEffect)
        {
            InterruptNextMoveAfterEffect = false;
            throw new DropboxEffectUncertainException(
                "fixture lost the native-move response after applying the effect");
        }
        return new PilotRemoteEntry(destinationRelativePath, PilotEntryKind.File, current.Id,
            "rev:" + current.Revision, current.Bytes.Length, await HashAsync(current.Bytes));
    }

    public async Task<PilotRemoteEntry> MoveToAsync(PilotRemoteEntry expected,
        ISyncRemoteStore destinationStore, string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destinationStore is not FakeSyncRemoteStore destination)
            throw new NotSupportedException("The fake provider only moves between fake roots.");
        if (InterruptNextMoveBeforeEffect)
        {
            InterruptNextMoveBeforeEffect = false;
            throw new DropboxEffectUncertainException(
                "fixture lost the cross-root move response before applying the effect");
        }
        if (expected.Kind == PilotEntryKind.Directory)
        {
            if (!_directories.TryGetValue(expected.RelativePath, out var directoryId) ||
                !string.Equals(directoryId, expected.Id, StringComparison.Ordinal) ||
                destination.HasPathOrDescendant(destinationRelativePath))
                throw new DropboxApiException(HttpStatusCode.Conflict, "cross-root folder move conflict");

            var directories = _directories.Where(item => IsSameOrDescendant(item.Key, expected.RelativePath))
                .ToArray();
            var files = _files.Where(item => IsDescendant(item.Key, expected.RelativePath)).ToArray();
            foreach (var item in directories) _directories.TryRemove(item.Key, out _);
            foreach (var item in files) _files.TryRemove(item.Key, out _);
            foreach (var item in directories)
                destination._directories[Rebase(item.Key, expected.RelativePath, destinationRelativePath)] = item.Value;
            foreach (var item in files)
                destination._files[Rebase(item.Key, expected.RelativePath, destinationRelativePath)] = item.Value;
            RecordChange(expected.RelativePath, "deleted");
            destination.RecordChange(destinationRelativePath, "folder");
            MoveCount++;
            if (InterruptNextMoveAfterEffect)
            {
                InterruptNextMoveAfterEffect = false;
                throw new DropboxEffectUncertainException(
                    "fixture lost the cross-root folder-move response after applying the effect");
            }
            return new PilotRemoteEntry(destinationRelativePath, PilotEntryKind.Directory, directoryId,
                null, 0, null);
        }
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "cross-root move source conflict");
        if (destination._files.ContainsKey(destinationRelativePath))
            throw new DropboxApiException(HttpStatusCode.Conflict, "cross-root move destination conflict");
        _files.TryRemove(expected.RelativePath, out _);
        destination._files[destinationRelativePath] = current;
        RecordChange(expected.RelativePath, "deleted");
        destination.RecordChange(destinationRelativePath, "file");
        MoveCount++;
        if (InterruptNextMoveAfterEffect)
        {
            InterruptNextMoveAfterEffect = false;
            throw new DropboxEffectUncertainException(
                "fixture lost the cross-root move response after applying the effect");
        }
        return new PilotRemoteEntry(destinationRelativePath, PilotEntryKind.File, current.Id,
            "rev:" + current.Revision, current.Bytes.Length, await HashAsync(current.Bytes));
    }

    private bool HasPathOrDescendant(string path) =>
        _directories.Keys.Any(item => IsSameOrDescendant(item, path)) ||
        _files.Keys.Any(item => IsSameOrDescendant(item, path));

    private static bool IsSameOrDescendant(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || IsDescendant(path, parent);

    private static bool IsDescendant(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static string Rebase(string path, string source, string destination) =>
        string.Equals(path, source, StringComparison.OrdinalIgnoreCase)
            ? destination
            : destination.TrimEnd('/') + path[source.TrimEnd('/').Length..];

    public Task<string> GetLatestCursorAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentCursor);

    public Task<SyncRemoteChangePage> ContinueChangesAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        if (InterruptNextChangePoll)
        {
            InterruptNextChangePoll = false;
            throw new IOException("simulated Dropbox monitor transport failure");
        }
        if (ResetNextCursor)
        {
            ResetNextCursor = false;
            throw new DropboxApiException(HttpStatusCode.Conflict, "fixture cursor reset");
        }
        if (CycleNextChangeCursorSequence)
        {
            var cycleCursor = cursor switch
            {
                "0" => "cursor-a",
                "cursor-a" => "cursor-b",
                "cursor-b" => "cursor-a",
                _ => throw new InvalidOperationException("Unexpected cursor in the forced cycle fixture.")
            };
            if (cursor == "cursor-b") CycleNextChangeCursorSequence = false;
            return Task.FromResult(new SyncRemoteChangePage(cycleCursor, true, []));
        }
        var after = long.Parse(cursor);
        (long Sequence, string? Path, string Kind)[] selected;
        long next;
        bool hasMore;
        lock (_changeGate)
        {
            selected = _changes.Where(item => item.Sequence > after).Take(1000).ToArray();
            next = selected.Length == 0 ? after : selected[^1].Sequence;
            hasMore = _changes.Any(item => item.Sequence > next);
        }
        var entries = selected.Select(item => new SyncRemoteChangeEntry(item.Path, item.Kind,
            JsonSerializer.Serialize(new { item.Path, item.Kind, item.Sequence }))).ToArray();
        return Task.FromResult(new SyncRemoteChangePage(next.ToString(), hasMore, entries));
    }

    private void RecordChange(string? path, string kind)
    {
        lock (_changeGate) _changes.Add((++_changeSequence, path, kind));
    }

    public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        if (!ExactPathMetadataForTest)
            return await SyncRemoteExactLookup.FindEntryAsync(this, relativePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ExactPathLookupCount++;
        var path = PilotPath.NormalizeRelative(relativePath);
        var files = _files.Where(item => string.Equals(item.Key, path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var directories = _directories.Where(item =>
            string.Equals(item.Key, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length + directories.Length == 0) return null;
        if (files.Length + directories.Length != 1)
            throw new IOException("The fixture returned more than one exact item at " + path + ".");
        if (files.Length == 1)
            return new PilotRemoteEntry(files[0].Key, PilotEntryKind.File, files[0].Value.Id,
                "rev:" + files[0].Value.Revision, files[0].Value.Bytes.Length,
                SuppressContentHashesForTest ? null : await HashAsync(files[0].Value.Bytes));
        return new PilotRemoteEntry(directories[0].Key, PilotEntryKind.Directory, directories[0].Value,
            null, 0, null);
    }

    public async Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken = default)
    {
        if (BeforeReadEntryForTest is not null)
            await BeforeReadEntryForTest(relativePath, cancellationToken);
        if (!ExactPathMetadataForTest)
            return await SyncRemoteExactLookup.RequireEntryAsync(this, relativePath, providerObjectId,
                cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ExactPathLookupCount++;
        var path = PilotPath.NormalizeRelative(relativePath);
        if (_files.TryGetValue(path, out var file) &&
            string.Equals(file.Id, providerObjectId, StringComparison.Ordinal))
            return new PilotRemoteEntry(path, PilotEntryKind.File, file.Id, "rev:" + file.Revision,
                file.Bytes.Length, SuppressContentHashesForTest ? null : await HashAsync(file.Bytes));
        if (_directories.TryGetValue(path, out var directoryId) &&
            string.Equals(directoryId, providerObjectId, StringComparison.Ordinal))
            return new PilotRemoteEntry(path, PilotEntryKind.Directory, directoryId, null, 0, null);
        throw new IOException("The fixture did not return one exact item at " + path + ".");
    }

    public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId,
        string namespaceId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(binding.MarkerContentHash,
                DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installationId, namespaceId)),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fake control namespace marker mismatch.");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default)
    {
        ListEntriesCount++;
        if (BeforeListEntriesForTest is not null)
            await BeforeListEntriesForTest(cancellationToken);
        try
        {
            var entries = new List<PilotRemoteEntry>();
            foreach (var item in _directories)
                entries.Add(new PilotRemoteEntry(item.Key, PilotEntryKind.Directory, item.Value, null, 0, null));
            foreach (var item in _files)
                entries.Add(new PilotRemoteEntry(item.Key, PilotEntryKind.File, item.Value.Id, "rev:" + item.Value.Revision,
                    item.Value.Bytes.Length, SuppressContentHashesForTest ? null : await HashAsync(item.Value.Bytes)));
            return entries;
        }
        finally { AfterListEntriesForTest?.Invoke(); }
    }

    public Task<PilotRemoteEntry> CreateDirectoryAsync(string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_files.ContainsKey(relativePath) || _directories.ContainsKey(relativePath))
            throw new DropboxApiException(HttpStatusCode.Conflict, "create folder conflict");
        var id = "id:folder:" + relativePath;
        _directories[relativePath] = id;
        Interlocked.Increment(ref _directoryCreateCount);
        RecordChange(relativePath, "folder");
        return Task.FromResult(new PilotRemoteEntry(relativePath, PilotEntryKind.Directory, id, null, 0, null));
    }

    public Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source, string expectedContentHash,
        CancellationToken cancellationToken = default) => UploadAsync(relativePath, source, expectedContentHash, null, cancellationToken);

    public async Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
        string? expectedRemoteRevision, CancellationToken cancellationToken = default)
    {
        if (UploadFailureForTest?.Invoke(relativePath) is { } failure) throw failure;
        if (InterruptNextUploadBeforeEffect)
        {
            InterruptNextUploadBeforeEffect = false;
            throw new DropboxEffectUncertainException("fixture returned an unknown upload status before applying the effect");
        }
        if (expectedRemoteRevision is null && _files.ContainsKey(relativePath))
            throw new DropboxApiException(HttpStatusCode.Conflict, "create conflict");
        if (expectedRemoteRevision is not null && (!_files.TryGetValue(relativePath, out var expected) ||
            !string.Equals("rev:" + expected.Revision, expectedRemoteRevision, StringComparison.Ordinal)))
            throw new DropboxApiException(HttpStatusCode.Conflict, "revision conflict");
        await using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (!string.Equals(expectedContentHash, await HashAsync(bytes), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fake sync upload hash mismatch.");
        var revision = _files.TryGetValue(relativePath, out var current) ? current.Revision + 1 : 1;
        var fileId = _files.TryGetValue(relativePath, out current) ? current.Id : "id:" + relativePath;
        _files[relativePath] = (bytes, revision, fileId);
        Interlocked.Increment(ref _uploadCount);
        if (InterruptNextUploadAfterEffect)
        {
            InterruptNextUploadAfterEffect = false;
            throw new DropboxEffectUncertainException("fixture interrupted after accepted upload");
        }
        return new PilotUploadResult(fileId, "rev:" + revision, bytes.Length, expectedContentHash);
    }

    public async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (BeforeDownloadForTest is not null)
            await BeforeDownloadForTest(cancellationToken);
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "download revision conflict");
        Interlocked.Increment(ref _downloadCount);
        if (DownloadFailureForTest?.Invoke(expected.RelativePath) is { } failedDownload)
        {
            await destination.WriteAsync(current.Bytes.AsMemory(0, Math.Max(1, current.Bytes.Length / 2)), cancellationToken);
            throw failedDownload;
        }
        if (InterruptNextDownload)
        {
            InterruptNextDownload = false;
            await destination.WriteAsync(current.Bytes.AsMemory(0, Math.Max(1, current.Bytes.Length / 2)), cancellationToken);
            throw new IOException("fixture interrupted download");
        }
        await destination.WriteAsync(current.Bytes, cancellationToken);
        destination.Position = 0;
        return new SyncDownloadResult(current.Id, "rev:" + current.Revision,
            current.Bytes.Length, await HashAsync(current.Bytes));
    }

    public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset,
        long length, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal) ||
            current.Bytes.LongLength != expected.Size || offset < 0 || length <= 0 ||
            offset > current.Bytes.LongLength || length > current.Bytes.LongLength - offset)
            throw new DropboxApiException(HttpStatusCode.Conflict, "range download identity conflict");
        cancellationToken.ThrowIfCancellationRequested();
        RangeDownloadCount++;
        RangeDownloadBytes = checked(RangeDownloadBytes + length);
        if (InterruptNextDownload)
        {
            InterruptNextDownload = false;
            var interruptedLength = checked((int)Math.Min(64 * 1024, Math.Max(1, length / 2)));
            await destination.WriteAsync(current.Bytes.AsMemory(checked((int)offset), interruptedLength),
                cancellationToken);
            throw new IOException("fixture interrupted range download");
        }
        for (long written = 0; written < length;)
        {
            var count = checked((int)Math.Min(128 * 1024, length - written));
            await destination.WriteAsync(current.Bytes.AsMemory(checked((int)(offset + written)), count),
                cancellationToken);
            written += count;
        }
        return new SyncRangeDownloadResult(current.Id, "rev:" + current.Revision,
            current.Bytes.LongLength, await HashAsync(current.Bytes), offset, length);
    }

    public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "archive revision conflict");
        if (LoseNextArchiveRecovery)
        {
            LoseNextArchiveRecovery = false;
            ArchiveCount++;
            throw new DropboxEffectUncertainException("fixture failed before creating the prebound recovery object");
        }
        var result = new SyncArchiveResult("id:recovery:" + recoveryPath, "rev:recovery:" + current.Revision,
            recoveryPath, current.Bytes.Length, expected.ContentHash!);
        _archive[recoveryPath] = result;
        ArchiveCount++;
        if (InterruptNextArchiveAfterEffect)
        {
            InterruptNextArchiveAfterEffect = false;
            throw new DropboxEffectUncertainException("fixture interrupted after the recoverable archive was accepted");
        }
        return Task.FromResult(result);
    }

    public Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_archive.TryGetValue(recoveryPath, out var retained) ? retained : null);

    public Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binding.RootPath) || string.IsNullOrWhiteSpace(binding.RootFolderId) ||
            string.IsNullOrWhiteSpace(binding.RootNamespaceId))
            throw new IOException("Fake recovery-root binding is incomplete.");
        return Task.CompletedTask;
    }

    public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "recovery source revision conflict");
        if (InterruptNextArchiveBeforeEffect)
        {
            InterruptNextArchiveBeforeEffect = false;
            throw new IOException("fixture interrupted before the bound recovery copy was applied");
        }
        var recoveryPath = binding.RootPath.TrimEnd('/') + "/" + deterministicName;
        if (_archive.ContainsKey(recoveryPath))
            throw new DropboxApiException(HttpStatusCode.Conflict, "recovery destination conflict");
        var result = new SyncArchiveResult("id:recovery:" + deterministicName,
            "rev:recovery:" + current.Revision, recoveryPath, current.Bytes.Length, expected.ContentHash!);
        _archive[recoveryPath] = result;
        ArchiveCount++;
        if (InterruptNextArchiveAfterEffect)
        {
            InterruptNextArchiveAfterEffect = false;
            throw new DropboxEffectUncertainException("fixture interrupted after the bound recovery copy was accepted");
        }
        return Task.FromResult(result);
    }

    public Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        string deterministicName, CancellationToken cancellationToken = default)
    {
        var recoveryPath = binding.RootPath.TrimEnd('/') + "/" + deterministicName;
        return Task.FromResult(_archive.TryGetValue(recoveryPath, out var retained) ? retained : null);
    }

    public async Task<PilotUploadResult> CopyCreateOnlyAsync(PilotRemoteEntry expected,
        string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "visible-copy source revision conflict");
        if (_files.ContainsKey(destinationRelativePath))
            throw new DropboxApiException(HttpStatusCode.Conflict, "visible-copy destination conflict");
        var hash = await HashAsync(current.Bytes);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fake visible-copy source hash mismatch.");
        var copiedBytes = current.Bytes.ToArray();
        var copiedId = "id:" + destinationRelativePath;
        _files[destinationRelativePath] = (copiedBytes, 1, copiedId);
        VisibleCopyCount++;
        if (InterruptNextVisibleCopyAfterEffect)
        {
            InterruptNextVisibleCopyAfterEffect = false;
            throw new DropboxEffectUncertainException("fixture interrupted after visible conflict copy");
        }
        return new PilotUploadResult(copiedId, "rev:1", copiedBytes.Length, hash);
    }

    public Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default)
    {
        if (expected.Kind == PilotEntryKind.Directory)
        {
            if (!_directories.TryGetValue(expected.RelativePath, out var directoryId) ||
                !string.Equals(directoryId, expected.Id, StringComparison.Ordinal) ||
                _directories.Keys.Any(path => path.StartsWith(expected.RelativePath + "/",
                    StringComparison.OrdinalIgnoreCase)) ||
                _files.Keys.Any(path => path.StartsWith(expected.RelativePath + "/",
                    StringComparison.OrdinalIgnoreCase)))
                throw new DropboxApiException(HttpStatusCode.Conflict, "delete folder identity conflict");
            _directories.TryRemove(expected.RelativePath, out _);
            DeleteCount++;
            if (InterruptNextDeleteAfterEffect)
            {
                InterruptNextDeleteAfterEffect = false;
                throw new DropboxEffectUncertainException(
                    "fixture interrupted after conditional folder deletion");
            }
            return Task.CompletedTask;
        }
        if (!_files.TryGetValue(expected.RelativePath, out var current) ||
            !string.Equals("rev:" + current.Revision, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(current.Id, expected.Id, StringComparison.Ordinal))
            throw new DropboxApiException(HttpStatusCode.Conflict, "delete revision conflict");
        _files.TryRemove(expected.RelativePath, out _);
        DeleteCount++;
        if (InterruptNextDeleteAfterEffect)
        {
            InterruptNextDeleteAfterEffect = false;
            throw new DropboxEffectUncertainException("fixture interrupted after conditional deletion");
        }
        return Task.CompletedTask;
    }

    private static async Task<string> HashAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await DropboxContentHash.ComputeAsync(stream);
    }
}

sealed class FixedTokenSource : IDropboxAccessTokenSource
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-token");
}

static class NativeTestFiles
{
    public static bool TryCreateHardLinkFromChild(string linkPath, string existingPath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--hard-link-probe");
        start.ArgumentList.Add(linkPath);
        start.ArgumentList.Add(existingPath);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The hard-link probe process did not start.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The hard-link probe did not reach a bounded result.");
        }
        if (process.ExitCode == 0) return true;
        if (process.ExitCode == 5) return false;
        throw new InvalidOperationException($"The hard-link probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
            throw new IOException("The test hard link could not be created.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
    }

    public static void RunAclCrashChild(string mode, string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--acl-crash-probe");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The ACL crash probe did not start.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The ACL crash probe did not reach its forced-termination boundary.");
        }
        if (process.ExitCode != 197)
            throw new InvalidOperationException($"The ACL crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void RunCleanupCrashChild(string scenario, string boundary, bool nearCeiling, string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cleanup-crash-probe");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(boundary);
        start.ArgumentList.Add(nearCeiling.ToString());
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The cleanup crash probe did not start.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The cleanup crash probe did not reach its forced-termination boundary.");
        }
        if (process.ExitCode != 198)
            throw new InvalidOperationException($"The cleanup crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void RunTrueSyncCrashChild(string boundary, string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--true-sync-crash-probe");
        start.ArgumentList.Add(boundary);
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The true-sync crash probe did not start.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The true-sync crash probe did not reach forced termination.");
        }
        if (process.ExitCode != 199)
            throw new InvalidOperationException($"The true-sync crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void RunRecoverySpoolCrashChild(string spoolRoot)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--recovery-spool-crash-probe");
        start.ArgumentList.Add(spoolRoot);
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("The recovery-spool crash probe did not start.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The recovery-spool crash probe did not reach forced termination.");
        }
        if (process.ExitCode != 213)
            throw new InvalidOperationException(
                $"The recovery-spool crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void RunCrossPairGenerationCrashChild(string direction, string scenarioRoot)
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cross-pair-generation-crash-probe");
        start.ArgumentList.Add(direction);
        start.ArgumentList.Add(Path.GetFullPath(scenarioRoot));
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException(
                                "The cross-pair generation crash probe did not start.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The cross-pair generation crash probe did not reach forced termination.");
        }
        if (process.ExitCode != 214)
            throw new InvalidOperationException(
                $"The cross-pair generation crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void RunCrossPairDirectoryEffectCrashChild(string direction, string scenarioRoot)
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("The test host executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cross-pair-directory-effect-crash-probe");
        start.ArgumentList.Add(direction);
        start.ArgumentList.Add(Path.GetFullPath(scenarioRoot));
        start.ArgumentList.Add(SyncSharkRuntimePaths.ProductRoot);
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException(
                                "The populated-directory effect crash probe did not start.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The populated-directory effect crash probe did not reach forced termination.");
        }
        if (process.ExitCode != 215)
            throw new InvalidOperationException(
                $"The populated-directory effect crash probe returned unexpected exit code {process.ExitCode}.");
    }

    public static void TerminateCurrentProcess(uint exitCode)
    {
        if (!TerminateProcess(GetCurrentProcess(), exitCode))
            throw new IOException("The ACL crash probe could not force process termination.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

sealed class DropboxUploadSessionContractHandler : HttpMessageHandler
{
    private readonly Dictionary<string, MemoryStream> _sessions = new(StringComparer.Ordinal);
    private int _nextSession;

    public bool LoseNextAppendAfterAccept { get; set; }
    public bool LoseNextFinishAfterAccept { get; set; }
    public int StartCount { get; private set; }
    public int AppendCount { get; private set; }
    public int FinishCount { get; private set; }
    public int CorrectOffsetCount { get; private set; }
    public byte[] CommittedBytes { get; private set; } = [];

    public void ExpireSessions()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
        if (route.EndsWith("/files/upload_session/start", StringComparison.Ordinal))
        {
            var id = "session-" + Interlocked.Increment(ref _nextSession);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var session = new MemoryStream();
            await session.WriteAsync(bytes, cancellationToken);
            _sessions.Add(id, session);
            StartCount++;
            return Json($"{{\"session_id\":\"{id}\"}}");
        }
        if (route.EndsWith("/files/upload_session/append_v2", StringComparison.Ordinal))
        {
            AppendCount++;
            var (sessionId, offset) = Cursor(request);
            if (!_sessions.TryGetValue(sessionId, out var session)) return MissingSession();
            if (offset != session.Length) return IncorrectOffset(session.Length);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            session.Position = session.Length;
            await session.WriteAsync(bytes, cancellationToken);
            if (LoseNextAppendAfterAccept)
            {
                LoseNextAppendAfterAccept = false;
                throw new HttpRequestException("simulated append acknowledgement loss");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
        if (route.EndsWith("/files/upload_session/finish", StringComparison.Ordinal))
        {
            FinishCount++;
            var (sessionId, offset) = Cursor(request);
            if (!_sessions.TryGetValue(sessionId, out var session)) return MissingSession();
            if (offset != session.Length) return IncorrectOffset(session.Length);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            session.Position = session.Length;
            await session.WriteAsync(bytes, cancellationToken);
            CommittedBytes = session.ToArray();
            _sessions.Remove(sessionId);
            session.Dispose();
            if (LoseNextFinishAfterAccept)
            {
                LoseNextFinishAfterAccept = false;
                throw new HttpRequestException("simulated finish acknowledgement loss");
            }
            await using var hashSource = new MemoryStream(CommittedBytes, writable: false);
            var hash = await DropboxContentHash.ComputeAsync(hashSource, cancellationToken);
            return Json($"{{\".tag\":\"file\",\"name\":\"large.bin\",\"id\":\"id:large\",\"rev\":\"rev:large\",\"size\":{CommittedBytes.Length},\"content_hash\":\"{hash}\"}}");
        }
        throw new InvalidOperationException("Unexpected upload-session route: " + route);
    }

    private static (string SessionId, long Offset) Cursor(HttpRequestMessage request)
    {
        using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
        var cursor = argument.RootElement.GetProperty("cursor");
        return (cursor.GetProperty("session_id").GetString()!, cursor.GetProperty("offset").GetInt64());
    }

    private HttpResponseMessage IncorrectOffset(long correctOffset)
    {
        CorrectOffsetCount++;
        return new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                $"{{\"error_summary\":\"incorrect_offset/..\",\"error\":{{\".tag\":\"incorrect_offset\",\"correct_offset\":{correctOffset}}}}}",
                Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage MissingSession() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(
            """{"error_summary":"lookup_failed/not_found/..","error":{".tag":"lookup_failed","lookup_failed":{".tag":"not_found"}}}""",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxRateLimitContractHandler : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (CallCount == 1)
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromMilliseconds(50));
            return Task.FromResult(limited);
        }
        var route = request.RequestUri!.AbsolutePath;
        var json = route.EndsWith("/users/get_current_account", StringComparison.Ordinal)
            ? """{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}"""
            : route.EndsWith("/files/get_metadata", StringComparison.Ordinal)
                ? """{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}"""
                : throw new InvalidOperationException("Unexpected Dropbox rate-limit fixture route: " + route);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

enum DropboxListingFailureMode
{
    PermissionDenied,
    InterruptedContinuation,
    RepeatedCursor,
    CursorCycle,
    OversizedCursor,
    UnsupportedName
}

sealed class DropboxListingFailureHandler(DropboxListingFailureMode mode) : HttpMessageHandler
{
    private int _continuationCalls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Task.FromResult(Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}"""));
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            return Task.FromResult(Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}"""));
        if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
        {
            if (mode == DropboxListingFailureMode.InterruptedContinuation)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("pagination interrupted")
                });
            if (mode == DropboxListingFailureMode.RepeatedCursor)
                return Task.FromResult(Json("""{"entries":[],"cursor":"cursor-repeat","has_more":true}"""));
            if (mode == DropboxListingFailureMode.CursorCycle)
            {
                _continuationCalls++;
                var cursor = _continuationCalls % 2 == 1 ? "cursor-b" : "cursor-a";
                return Task.FromResult(Json($"{{\"entries\":[],\"cursor\":\"{cursor}\",\"has_more\":true}}"));
            }
            throw new InvalidOperationException("The listing fixture received an unexpected continuation.");
        }
        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
        {
            if (mode == DropboxListingFailureMode.PermissionDenied)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("sharing or permission denied")
                });
            if (mode == DropboxListingFailureMode.UnsupportedName)
                return Task.FromResult(Json("""{"entries":[{".tag":"file","name":"CON.txt","id":"id:unsupported","rev":"rev:1","size":0,"content_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","path_lower":"/pilot/con.txt","path_display":"/Pilot/CON.txt"}],"cursor":"cursor-done","has_more":false}"""));
            if (mode == DropboxListingFailureMode.OversizedCursor)
                return Task.FromResult(Json($"{{\"entries\":[],\"cursor\":\"{new string('c', 16 * 1024 + 1)}\",\"has_more\":true}}"));
            if (mode == DropboxListingFailureMode.CursorCycle)
                return Task.FromResult(Json("""{"entries":[],"cursor":"cursor-a","has_more":true}"""));
            return Task.FromResult(Json("""{"entries":[{".tag":"file","name":"first.txt","id":"id:first","rev":"rev:1","size":0,"content_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","path_lower":"/pilot/first.txt","path_display":"/Pilot/first.txt"}],"cursor":"cursor-repeat","has_more":true}"""));
        }
        throw new InvalidOperationException("Unexpected Dropbox listing fixture route: " + route);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxContractHandler(bool nonempty = false, bool rootEcho = false, bool accountRoot = false,
    bool mountedDescendants = false) : HttpMessageHandler
{
    public string? PathRootTag { get; private set; }
    public string? PathRootNamespace { get; private set; }
    public string? UploadMode { get; private set; }
    public string? UploadPath { get; private set; }
    public string? UploadUpdateRevision { get; private set; }
    public bool UploadAutorename { get; private set; }
    public bool UploadStrictConflict { get; private set; }
    public int PathMetadataRequests { get; private set; }
    public byte[] DownloadBytes { get; set; } = [];
    public string? DownloadHash { get; set; }
    public string DownloadRevision { get; set; } = "rev:download";
    public string MetadataRevision { get; set; } = "rev:archive";
    public string? DownloadPath { get; private set; }
    public string? DownloadRequestedRevision { get; private set; }
    public int DownloadRequests { get; private set; }
    public string[] DownloadArgumentProperties { get; private set; } = [];
    public string? ArchiveHash { get; set; }
    public long ArchiveSize { get; set; }
    public string? ArchiveFromPath { get; private set; }
    public string? ArchiveToPath { get; private set; }
    public Func<int, string>? WireMetadataPathForRequest { get; init; }
    public int WireMetadataRequests { get; private set; }
    public int DeleteCalls { get; private set; }
    public string? DeletePath { get; private set; }
    public HttpStatusCode UploadStatusCode { get; init; } = HttpStatusCode.OK;
    public HttpStatusCode DeleteStatusCode { get; init; } = HttpStatusCode.OK;
    public string? DeleteSuccessBodyOverride { get; init; }
    public bool ThrowOnDeleteBodyRead { get; init; }
    public bool OmitDownloadMetadataPaths { get; init; }
    public string? ListPath { get; private set; }
    public bool ListIncludesMounted { get; private set; }
    public bool CursorIncludesMounted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var rootValues))
        {
            using var pathRoot = JsonDocument.Parse(rootValues.Single());
            PathRootTag = pathRoot.RootElement.GetProperty(".tag").GetString();
            PathRootNamespace = pathRoot.RootElement.GetProperty("root").GetString();
        }

        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");

        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var path = document.RootElement.GetProperty("path").GetString();
            if (path is "/Pilot" or "id:pilot-folder")
            {
                PathMetadataRequests++;
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            }
            if (path == "id:wire")
            {
                WireMetadataRequests++;
                var display = WireMetadataPathForRequest?.Invoke(WireMetadataRequests) ?? "/Pilot/wire.txt";
                return Json($"{{\".tag\":\"file\",\"name\":\"wire.txt\",\"id\":\"id:wire\",\"rev\":\"{MetadataRevision}\",\"size\":{ArchiveSize},\"content_hash\":\"{ArchiveHash}\",\"path_lower\":\"{display.ToLowerInvariant()}\",\"path_display\":\"{display}\"}}");
            }
            if (path == "/Apps/Sync Shark/Recovery")
                return Json("""{".tag":"folder","name":"Recovery","id":"id:recovery-root","path_lower":"/apps/sync shark/recovery","path_display":"/Apps/Sync Shark/Recovery"}""");
            if (path == "/Apps/Sync Shark/Recovery/pair-identifier")
                return Json("""{".tag":"folder","name":"pair-identifier","id":"id:recovery-pair","path_lower":"/apps/sync shark/recovery/pair-identifier","path_display":"/Apps/Sync Shark/Recovery/pair-identifier"}""");
            throw new InvalidOperationException("Unexpected metadata path: " + path);
        }

        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
        {
            using var listDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ListPath = listDocument.RootElement.GetProperty("path").GetString();
            ListIncludesMounted = listDocument.RootElement.GetProperty("include_mounted_folders").GetBoolean();
            if (mountedDescendants)
            {
                if (!ListIncludesMounted)
                    return Json("""{"entries":[],"cursor":"mounted-cursor","has_more":false}""");
                if (listDocument.RootElement.GetProperty("include_deleted").GetBoolean())
                    return Json("""{"entries":[{".tag":"deleted","name":"deleted.txt","path_lower":"/pilot/mounted/deleted.txt","path_display":"/Pilot/Mounted/deleted.txt"}],"cursor":"mounted-cursor","has_more":false}""");
                return Json("""{"entries":[{".tag":"folder","name":"Mounted","id":"id:mounted","path_lower":"/pilot/mounted","path_display":"/Pilot/Mounted"},{".tag":"file","name":"shared.txt","id":"id:mounted-file","rev":"rev:mounted","size":0,"content_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","path_lower":"/pilot/mounted/shared.txt","path_display":"/Pilot/Mounted/shared.txt"}],"cursor":"mounted-cursor","has_more":false}""");
            }
            return accountRoot
                ? Json("""{"entries":[{".tag":"folder","name":"Documents","id":"id:documents","path_lower":"/documents","path_display":"/Documents"}],"cursor":"cursor","has_more":false}""")
                : rootEcho
                ? Json("""{"entries":[{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}],"cursor":"cursor","has_more":false}""")
                : nonempty
                ? Json("""{"entries":[{".tag":"file","name":"existing.txt","id":"id:existing","rev":"rev:existing","size":0,"content_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","path_lower":"/pilot/existing.txt","path_display":"/Pilot/existing.txt"}],"cursor":"cursor","has_more":false}""")
                : Json("""{"entries":[],"cursor":"cursor","has_more":false}""");
        }

        if (route.EndsWith("/files/list_folder/get_latest_cursor", StringComparison.Ordinal))
        {
            using var cursorDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CursorIncludesMounted = cursorDocument.RootElement.GetProperty("include_mounted_folders").GetBoolean();
            return mountedDescendants ? Json("""{"cursor":"mounted-cursor"}""") : Json("""{"cursor":"cursor"}""");
        }

        if (route.EndsWith("/files/upload", StringComparison.Ordinal))
        {
            var raw = request.Headers.GetValues("Dropbox-API-Arg").Single();
            using var argument = JsonDocument.Parse(raw);
            var mode = argument.RootElement.GetProperty("mode");
            if (mode.ValueKind == JsonValueKind.String) UploadMode = mode.GetString();
            else
            {
                UploadMode = mode.GetProperty(".tag").GetString();
                UploadUpdateRevision = mode.GetProperty("update").GetString();
            }
            UploadPath = argument.RootElement.GetProperty("path").GetString();
            UploadAutorename = argument.RootElement.GetProperty("autorename").GetBoolean();
            UploadStrictConflict = argument.RootElement.GetProperty("strict_conflict").GetBoolean();
            await using var content = await request.Content!.ReadAsStreamAsync(cancellationToken);
            var hash = await DropboxContentHash.ComputeAsync(content, cancellationToken);
            var size = request.Content.Headers.ContentLength ?? content.Length;
            if (UploadStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(UploadStatusCode) { Content = new StringContent("status unknown") };
            return Json($"{{\".tag\":\"file\",\"name\":\"wire.txt\",\"id\":\"id:wire\",\"rev\":\"rev:wire\",\"size\":{size},\"content_hash\":\"{hash}\"}}");
        }

        if (route.EndsWith("/files/download", StringComparison.Ordinal))
        {
            DownloadRequests++;
            var raw = request.Headers.GetValues("Dropbox-API-Arg").Single();
            using var argument = JsonDocument.Parse(raw);
            DownloadArgumentProperties = argument.RootElement.EnumerateObject().Select(item => item.Name)
                .Order(StringComparer.Ordinal).ToArray();
            DownloadPath = argument.RootElement.GetProperty("path").GetString();
            DownloadRequestedRevision = argument.RootElement.TryGetProperty("rev", out var revision)
                ? revision.GetString()
                : null;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes) };
            var paths = OmitDownloadMetadataPaths
                ? string.Empty
                : ",\"path_lower\":\"/pilot/wire.txt\",\"path_display\":\"/Pilot/wire.txt\"";
            response.Headers.Add("Dropbox-API-Result",
                $"{{\"name\":\"wire.txt\",\"id\":\"id:wire\",\"rev\":\"{DownloadRevision}\",\"size\":{DownloadBytes.Length},\"content_hash\":\"{DownloadHash}\"{paths}}}");
            return response;
        }

        if (route.EndsWith("/files/create_folder_v2", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var path = document.RootElement.GetProperty("path").GetString();
            return path == "/Apps/Sync Shark/Recovery"
                ? Json("""{"metadata":{"name":"Recovery","id":"id:recovery-root","path_lower":"/apps/sync shark/recovery","path_display":"/Apps/Sync Shark/Recovery"}}""")
                : Json("""{"metadata":{"name":"pair-identifier","id":"id:recovery-pair","path_lower":"/apps/sync shark/recovery/pair-identifier","path_display":"/Apps/Sync Shark/Recovery/pair-identifier"}}""");
        }

        if (route.EndsWith("/files/move_v2", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            ArchiveFromPath = document.RootElement.GetProperty("from_path").GetString();
            ArchiveToPath = document.RootElement.GetProperty("to_path").GetString();
            var name = ArchiveToPath!.Split('/').Last();
            return Json($"{{\"metadata\":{{\".tag\":\"file\",\"name\":\"{name}\",\"id\":\"id:wire\",\"rev\":\"rev:archive\",\"size\":{ArchiveSize},\"content_hash\":\"{ArchiveHash}\",\"path_display\":\"/Apps/Sync Shark/Recovery/pair-identifier/{name}\"}}}}");
        }

        if (route.EndsWith("/files/delete_v2", StringComparison.Ordinal))
        {
            DeleteCalls++;
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                DeletePath = body.RootElement.GetProperty("path").GetString();
            if (DeleteStatusCode != HttpStatusCode.OK)
                return new HttpResponseMessage(DeleteStatusCode)
                {
                    Content = new StringContent("conditional deletion status unknown")
                };
            if (ThrowOnDeleteBodyRead)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingReadContent() };
            if (DeleteSuccessBodyOverride is not null)
                return Json(DeleteSuccessBodyOverride);
            return Json($"{{\"metadata\":{{\".tag\":\"file\",\"name\":\"wire.txt\",\"id\":\"id:wire\",\"rev\":\"rev:archive\",\"size\":{ArchiveSize},\"content_hash\":\"{ArchiveHash}\"}}}}");
        }

        throw new InvalidOperationException("Unexpected route: " + route);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class ThrowingReadContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.FromException(new IOException("simulated response-body interruption"));

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}

sealed class DropboxRecoveryBrowserContractHandler : HttpMessageHandler
{
    public const string HistoricalRevision = "historical-revision";
    private const string PairRootPath = "/Recovery Contract";
    private const string PairRootId = "id:recovery-contract";
    private const string RecoveryRootPath = "/Apps/Sync Shark/Recovery";
    private const string RecoveryRootId = "id:recovery-root";
    private readonly Dictionary<string, RecoveryContractFile> _files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecoveryContractFile> _revisions =
        new(StringComparer.Ordinal);
    private string? _archiveId;
    private int _nextIdentity;

    public int DeletedContinuationCount { get; private set; }
    public int RevisionDownloadCount { get; private set; }
    public int ArchiveRestoreCopyCount { get; private set; }
    public int ConditionalDeleteCount { get; private set; }
    public string? RetainedPreimageText { get; private set; }

    public void Configure(SyncRecoveryArtifactProjection artifact, byte[] archiveBytes,
        byte[] currentBytes, byte[] historicalBytes)
    {
        using var document = JsonDocument.Parse(artifact.IdentityJson);
        var archivePath = RequiredString(document.RootElement, "RecoveryPath");
        _archiveId = RequiredString(document.RootElement, "FileId");
        var archiveRevision = RequiredString(document.RootElement, "Revision");
        MustEqual(artifact.ContentHash, DropboxContentHash.Compute(archiveBytes));
        _files[archivePath] = new RecoveryContractFile(_archiveId, archiveRevision, archivePath,
            archiveBytes.ToArray(), new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));

        var current = new RecoveryContractFile("id:recovery-browser-original", "current-revision",
            PairRootPath + "/recover-me.txt", currentBytes.ToArray(),
            new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero));
        var historical = current with
        {
            Revision = HistoricalRevision,
            Bytes = historicalBytes.ToArray(),
            ModifiedUtc = new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero)
        };
        _files[current.Path] = current;
        _revisions[current.Revision] = current;
        _revisions[historical.Revision] = historical;
    }

    public string ReadPairText(string relativePath)
    {
        var path = PairRootPath + "/" + PilotPath.NormalizeRelative(relativePath);
        return Encoding.UTF8.GetString(_files[path].Bytes);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/oauth2/token", StringComparison.Ordinal))
            return Json(new
            {
                access_token = "recovery-contract-access-token",
                token_type = "bearer",
                expires_in = 14400,
                account_id = "account-id"
            });

        if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var pathRoots))
        {
            using var pathRoot = JsonDocument.Parse(pathRoots.Single());
            MustEqual("root", pathRoot.RootElement.GetProperty(".tag").GetString());
            MustEqual("root-namespace", pathRoot.RootElement.GetProperty("root").GetString());
        }

        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json(new
            {
                account_id = "account-id",
                root_info = new
                {
                    root_namespace_id = "root-namespace",
                    home_namespace_id = "home-namespace"
                }
            });

        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            var path = await BodyStringAsync(request, "path", cancellationToken);
            if (path is PairRootId or PairRootPath)
                return Json(FolderMetadata("Recovery Contract", PairRootId, PairRootPath));
            if (path is RecoveryRootId or RecoveryRootPath)
                return Json(FolderMetadata("Recovery", RecoveryRootId, RecoveryRootPath));
            var file = _files.Values.SingleOrDefault(item =>
                string.Equals(item.Id, path, StringComparison.Ordinal) ||
                string.Equals(item.Path, NormalizeApiPath(path), StringComparison.OrdinalIgnoreCase));
            return file is null ? NotFound() : Json(FileMetadata(file));
        }

        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
        {
            using var body = await BodyAsync(request, cancellationToken);
            var path = body.RootElement.GetProperty("path").GetString();
            var includeDeleted = body.RootElement.TryGetProperty("include_deleted", out var deleted) &&
                                 deleted.GetBoolean();
            if (path == PairRootId && includeDeleted)
                return Json(new
                {
                    entries = new object[] { DeletedMetadata("deleted-first.txt") },
                    cursor = "deleted-cursor-1",
                    has_more = true
                });
            if (path == PairRootId)
                return Json(new
                {
                    entries = _files.Values.Where(item => IsPairPath(item.Path))
                        .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(FileMetadata).ToArray(),
                    cursor = "pair-current-cursor",
                    has_more = false
                });
            if (path == RecoveryRootId)
                return Json(new
                {
                    entries = _files.Values.Where(item => item.Path.StartsWith(RecoveryRootPath + "/",
                            StringComparison.OrdinalIgnoreCase)).Select(FileMetadata).ToArray(),
                    cursor = "recovery-current-cursor",
                    has_more = false
                });
            throw new InvalidOperationException("Unexpected Dropbox list root: " + path);
        }

        if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
        {
            var cursor = await BodyStringAsync(request, "cursor", cancellationToken);
            if (cursor != "deleted-cursor-1")
                throw new InvalidOperationException("Unexpected Dropbox continuation cursor: " + cursor);
            DeletedContinuationCount++;
            return Json(new
            {
                entries = new object[] { DeletedMetadata("deleted-second.txt") },
                cursor = "deleted-cursor-complete",
                has_more = false
            });
        }

        if (route.EndsWith("/files/list_revisions", StringComparison.Ordinal))
        {
            var path = NormalizeApiPath(await BodyStringAsync(request, "path", cancellationToken));
            MustEqual(PairRootPath + "/recover-me.txt", path);
            return Json(new
            {
                entries = _revisions.Values.OrderByDescending(item => item.ModifiedUtc)
                    .Select(FileMetadata).ToArray(),
                is_deleted = false
            });
        }

        if (route.EndsWith("/files/download", StringComparison.Ordinal))
        {
            using var argument = HeaderArgument(request);
            var path = argument.RootElement.GetProperty("path").GetString() ?? string.Empty;
            if (!path.StartsWith("rev:", StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery download was not revision-bound.");
            var revision = path["rev:".Length..];
            var file = _revisions.TryGetValue(revision, out var historical)
                ? historical
                : _files.Values.SingleOrDefault(item => string.Equals(item.Revision, revision,
                    StringComparison.Ordinal));
            if (file is null) return NotFound();
            RevisionDownloadCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file.Bytes)
            };
            var downloadMetadata = FileMetadata(file);
            downloadMetadata.Remove(".tag");
            response.Headers.Add("Dropbox-API-Result", JsonSerializer.Serialize(downloadMetadata));
            return response;
        }

        if (route.EndsWith("/files/upload", StringComparison.Ordinal))
        {
            using var argument = HeaderArgument(request);
            var path = NormalizeApiPath(argument.RootElement.GetProperty("path").GetString() ?? string.Empty);
            if (_files.ContainsKey(path)) return Conflict();
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var file = new RecoveryContractFile("id:recovery-upload-" + ++_nextIdentity,
                "upload-revision-" + _nextIdentity, path, bytes,
                new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero));
            _files[path] = file;
            if (path.StartsWith(RecoveryRootPath + "/restore-current-", StringComparison.OrdinalIgnoreCase))
                RetainedPreimageText = Encoding.UTF8.GetString(bytes);
            return Json(FileMetadata(file));
        }

        if (route.EndsWith("/files/delete_v2", StringComparison.Ordinal))
        {
            using var body = await BodyAsync(request, cancellationToken);
            var exactPath = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
            MustEqual(PairRootId + "/recover-me.txt", exactPath);
            var revision = body.RootElement.GetProperty("parent_rev").GetString();
            var file = _files.Values.SingleOrDefault(item =>
                string.Equals(item.Path, NormalizeApiPath(exactPath), StringComparison.OrdinalIgnoreCase));
            if (file is null || !string.Equals(file.Revision, revision, StringComparison.Ordinal))
                return Conflict();
            _files.Remove(file.Path);
            ConditionalDeleteCount++;
            return Json(new { metadata = FileMetadata(file) });
        }

        if (route.EndsWith("/files/copy_v2", StringComparison.Ordinal))
        {
            using var body = await BodyAsync(request, cancellationToken);
            var sourceId = body.RootElement.GetProperty("from_path").GetString();
            var targetPath = NormalizeApiPath(body.RootElement.GetProperty("to_path").GetString() ?? string.Empty);
            if (!string.Equals(sourceId, _archiveId, StringComparison.Ordinal) || _files.ContainsKey(targetPath))
                return Conflict();
            var source = _files.Values.Single(item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
            var restored = new RecoveryContractFile("id:archive-restored", "archive-restored-revision",
                targetPath, source.Bytes.ToArray(), new DateTimeOffset(2026, 9, 5, 2, 1, 0, TimeSpan.Zero));
            _files[targetPath] = restored;
            ArchiveRestoreCopyCount++;
            return Json(new { metadata = FileMetadata(restored) });
        }

        throw new InvalidOperationException("Unexpected Dropbox recovery route: " + route);
    }

    private static bool IsPairPath(string path) =>
        path.StartsWith(PairRootPath + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeApiPath(string path) => path.StartsWith(PairRootId + "/",
        StringComparison.Ordinal) ? PairRootPath + path[PairRootId.Length..] : path;

    private static Dictionary<string, object?> FolderMetadata(string name, string id, string path) => new()
    {
        [".tag"] = "folder",
        ["name"] = name,
        ["id"] = id,
        ["path_lower"] = path.ToLowerInvariant(),
        ["path_display"] = path
    };

    private static Dictionary<string, object?> FileMetadata(RecoveryContractFile file) => new()
    {
        [".tag"] = "file",
        ["name"] = file.Path.Split('/').Last(),
        ["id"] = file.Id,
        ["rev"] = file.Revision,
        ["size"] = file.Bytes.LongLength,
        ["content_hash"] = DropboxContentHash.Compute(file.Bytes),
        ["path_lower"] = file.Path.ToLowerInvariant(),
        ["path_display"] = file.Path,
        ["server_modified"] = file.ModifiedUtc.ToUniversalTime().ToString("O")
    };

    private static Dictionary<string, object?> DeletedMetadata(string name) => new()
    {
        [".tag"] = "deleted",
        ["name"] = name,
        ["path_lower"] = (PairRootPath + "/" + name).ToLowerInvariant(),
        ["path_display"] = PairRootPath + "/" + name
    };

    private static async Task<JsonDocument> BodyAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));

    private static async Task<string> BodyStringAsync(HttpRequestMessage request, string property,
        CancellationToken cancellationToken)
    {
        using var body = await BodyAsync(request, cancellationToken);
        return body.RootElement.GetProperty(property).GetString() ??
               throw new InvalidDataException("Dropbox request lacks " + property + ".");
    }

    private static JsonDocument HeaderArgument(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException("Recovery artifact lacks " + name + ".");

    private static void MustEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidDataException($"Dropbox recovery contract mismatch. Expected {expected}; actual {actual}.");
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent("{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Conflict() => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent("{\"error_summary\":\"path/conflict/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"conflict\"}}}",
            Encoding.UTF8, "application/json")
    };

    private sealed record RecoveryContractFile(string Id, string Revision, string Path, byte[] Bytes,
        DateTimeOffset ModifiedUtc);
}

sealed class NoNetworkHttpHandler : HttpMessageHandler
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        throw new IOException("The offline recovery contract attempted a provider request.");
    }
}

sealed class MemoryDropboxRefreshTokenStore(string? value = null) : IDropboxRefreshTokenStore
{
    private string? _value = value;

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_value);
    }

    public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        _value = refreshToken;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _value = null;
        return Task.CompletedTask;
    }
}

sealed class FixtureAuthorizationReceiver : IDropboxAuthorizationReceiver
{
    public Task<DropboxLoopbackSession> ReceiveAsync(Uri redirectUri, string expectedState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (redirectUri.AbsoluteUri != DropboxProviderConfiguration.RegisteredRedirectUri ||
            string.IsNullOrWhiteSpace(expectedState))
            throw new InvalidDataException("The authorization receiver was not bound to the exact callback and state.");
        return Task.FromResult(new DropboxLoopbackSession("fixture-authorization-code"));
    }
}

sealed class DropboxConnectionLifecycleHandler(
    string accountId,
    string rootNamespaceId,
    string homeNamespaceId) : HttpMessageHandler
{
    public string AccountId { get; set; } = accountId;
    public string RootNamespaceId { get; set; } = rootNamespaceId;
    public string HomeNamespaceId { get; set; } = homeNamespaceId;
    public int ExpiresInSeconds { get; set; } = 3600;
    public int RefreshRequests { get; private set; }
    public bool ThrowOnRevoke { get; set; }
    public bool OversizedCurrentAccountResponse { get; set; }
    public HttpStatusCode CurrentAccountStatus { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/oauth2/token", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var refresh = body.Contains("grant_type=refresh_token", StringComparison.Ordinal);
            if (refresh) RefreshRequests++;
            var scopes = string.Join(' ', DropboxProviderConfiguration.RequiredScopes);
            var refreshField = refresh ? string.Empty : $",\"refresh_token\":\"refresh-{AccountId}\"";
            return Json($"{{\"access_token\":\"access-{AccountId}-{RefreshRequests}\"{refreshField},\"expires_in\":{ExpiresInSeconds},\"account_id\":\"{AccountId}\",\"scope\":\"{scopes}\"}}");
        }
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
        {
            if (CurrentAccountStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(CurrentAccountStatus)
                {
                    Content = new StringContent("provider authorization revoked")
                };
            if (OversizedCurrentAccountResponse)
                return Json("{\"padding\":\"" + new string('x', 1024 * 1024) + "\"}");
            return Json($"{{\"account_id\":\"{AccountId}\",\"root_info\":{{\"root_namespace_id\":\"{RootNamespaceId}\",\"home_namespace_id\":\"{HomeNamespaceId}\"}},\"name\":{{\"display_name\":\"Fixture Account\"}},\"email\":\"fixture@example.invalid\",\"account_type\":{{\".tag\":\"basic\"}}}}");
        }
        if (route.EndsWith("/auth/token/revoke", StringComparison.Ordinal))
        {
            if (ThrowOnRevoke) throw new IOException("simulated offline revocation");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        throw new InvalidOperationException("Unexpected connection lifecycle route: " + route);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class InMemoryDropboxFolderBrowser(int mismatchedCreateOrdinal = 0) : IDropboxFolderBrowserService
{
    private readonly Dictionary<string, List<DropboxFolderEntry>> _children =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DropboxFolderEntry> _byId = new(StringComparer.Ordinal);
    public int CreateCount { get; private set; }

    public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DropboxFolderPage(Children(folderId), null));
    }

    public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The in-memory Recovery browser does not paginate.");

    public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = _byId.Values.Where(item => item.Name.Contains(query,
            StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(new DropboxFolderPage(results, null));
    }

    public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The in-memory Recovery browser does not paginate search.");

    public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var folder in Children(folderId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return folder;
        }
        await Task.CompletedTask;
    }

    public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var siblings = Children(parentFolderId);
        if (siblings.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The in-memory Recovery folder already exists.");
        var parentPath = parentFolderId is null ? string.Empty : _byId[parentFolderId].Path;
        var ordinal = ++CreateCount;
        var entry = new DropboxFolderEntry(name,
            ordinal == mismatchedCreateOrdinal ? "/Outside/" + name : parentPath + "/" + name,
            "id:folder:" + ordinal);
        _byId.Add(entry.FolderId, entry);
        var key = parentFolderId ?? string.Empty;
        if (!_children.TryGetValue(key, out var list)) _children.Add(key, list = []);
        list.Add(entry);
        return Task.FromResult(entry);
    }

    public string SeedFolder(string? parentFolderId, string name, string path, string folderId)
    {
        var entry = new DropboxFolderEntry(name, path, folderId);
        _byId.Add(folderId, entry);
        var key = parentFolderId ?? string.Empty;
        if (!_children.TryGetValue(key, out var list)) _children.Add(key, list = []);
        list.Add(entry);
        return folderId;
    }

    private DropboxFolderEntry[] Children(string? folderId) =>
        _children.TryGetValue(folderId ?? string.Empty, out var entries)
            ? entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
}

sealed class PairBindingIdentityHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/oauth2/token", StringComparison.Ordinal))
            return Json(new { access_token = "binding-access", token_type = "bearer", expires_in = 14400 });
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json(new { account_id = "account-id", root_info = new
            { root_namespace_id = "root-namespace", home_namespace_id = "home-namespace" } });
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var path = document.RootElement.GetProperty("path").GetString();
            return path switch
            {
                "id:product-like" => Json(Folder("Sync Shark customer files", "/Sync Shark customer files",
                    "id:product-like")),
                "id:recovery-root" => Json(Folder("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "id:recovery-root")),
                _ => throw new InvalidOperationException("Unexpected pair-binding identity path: " + path)
            };
        }
        throw new InvalidOperationException("Unexpected pair-binding route: " + route);
    }

    private static object Folder(string name, string path, string id) => new Dictionary<string, object>
    {
        [".tag"] = "folder", ["name"] = name, ["path_display"] = path,
        ["path_lower"] = path.ToLowerInvariant(), ["id"] = id
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
}

sealed class DropboxBrowserContractHandler(bool nonFolderCreate = false, bool mismatchedReadback = false) : HttpMessageHandler
{
    public string? PathRootTag { get; private set; }
    public string? PathRootNamespace { get; private set; }
    public string? CreatedPath { get; private set; }
    public bool? IncludeMountedFolders { get; private set; }
    public int? ListLimit { get; private set; }
    public string? LastListPath { get; private set; }
    public string? SearchQuery { get; private set; }
    public string? SearchPath { get; private set; }
    public int? SearchLimit { get; private set; }
    public bool SearchContinueCalled { get; private set; }
    public bool CreateReadbackCalled { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var rootValues))
        {
            using var pathRoot = JsonDocument.Parse(rootValues.Single());
            PathRootTag = pathRoot.RootElement.GetProperty(".tag").GetString();
            PathRootNamespace = pathRoot.RootElement.GetProperty("root").GetString();
        }
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
        {
            using var listRequest = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastListPath = listRequest.RootElement.GetProperty("path").GetString();
            IncludeMountedFolders = listRequest.RootElement.GetProperty("include_mounted_folders").GetBoolean();
            ListLimit = listRequest.RootElement.GetProperty("limit").GetInt32();
            return Json("""{"entries":[{".tag":"file","name":"ignored.txt","id":"id:file","path_display":"/ignored.txt"},{".tag":"folder","name":"Alpha","id":"id:alpha","path_lower":"/alpha","path_display":"/Alpha"}],"cursor":"cursor-1","has_more":true}""");
        }
        if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
            return Json("""{"entries":[{".tag":"folder","name":"Beta","id":"id:beta","path_lower":"/beta","path_display":"/Beta"}],"cursor":"cursor-2","has_more":false}""");
        if (route.EndsWith("/files/search_v2", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            SearchQuery = document.RootElement.GetProperty("query").GetString();
            var options = document.RootElement.GetProperty("options");
            SearchPath = options.GetProperty("path").GetString();
            SearchLimit = options.GetProperty("max_results").GetInt32();
            return Json("""{"matches":[{"match_type":{".tag":"filename"},"metadata":{".tag":"metadata","metadata":{".tag":"folder","name":"Reports","id":"id:reports","path_lower":"/alpha/reports","path_display":"/Alpha/Reports"}}},{"match_type":{".tag":"filename"},"metadata":{".tag":"metadata","metadata":{".tag":"file","name":"reports.txt","id":"id:reports-file","path_lower":"/alpha/reports.txt","path_display":"/Alpha/reports.txt","rev":"1","size":1}}}],"has_more":true,"cursor":"search-cursor-1"}""");
        }
        if (route.EndsWith("/files/search/continue_v2", StringComparison.Ordinal))
        {
            SearchContinueCalled = true;
            return Json("""{"matches":[{"match_type":{".tag":"filename"},"metadata":{".tag":"metadata","metadata":{".tag":"folder","name":"Reports Archive","id":"id:reports-archive","path_lower":"/alpha/reports-archive","path_display":"/Alpha/Reports Archive"}}}],"has_more":false}""");
        }
        if (route.EndsWith("/files/create_folder_v2", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CreatedPath = document.RootElement.GetProperty("path").GetString();
            return nonFolderCreate
                ? Json("""{"metadata":{".tag":"file","name":"Wrong","id":"id:wrong","path_lower":"/wrong","path_display":"/Wrong"}}""")
                : Json("""{"metadata":{"name":"Child","id":"id:child","path_lower":"/alpha/child","path_display":"/Alpha/Child","property_groups":[]}}""");
        }
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            CreateReadbackCalled = true;
            return mismatchedReadback
                ? Json("""{".tag":"folder","name":"Other","id":"id:child","path_lower":"/alpha/other","path_display":"/Alpha/Other"}""")
                : Json("""{".tag":"folder","name":"Child","id":"id:child","path_lower":"/alpha/child","path_display":"/Alpha/Child"}""");
        }
        throw new InvalidOperationException("Unexpected browser route: " + route);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxBrowserCeilingHandler : HttpMessageHandler
{
    private int _page;
    public int ContinueCalls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Task.FromResult(Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}"""));
        if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal)) ContinueCalls++;
        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal) || route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
        {
            var page = _page++;
            return Task.FromResult(Json(BuildPage(page, page < 10)));
        }
        throw new InvalidOperationException("Unexpected ceiling route: " + route);
    }

    private static string BuildPage(int page, bool hasMore)
    {
        var entries = new StringBuilder(300_000);
        for (var index = 0; index < 2_000; index++)
        {
            if (index != 0) entries.Append(',');
            entries.Append($"{{\".tag\":\"folder\",\"name\":\"Folder {page}-{index}\",\"id\":\"id:{page}-{index}\",\"path_lower\":\"/folder-{page}-{index}\",\"path_display\":\"/Folder {page}-{index}\"}}");
        }
        return $"{{\"entries\":[{entries}],\"cursor\":\"cursor-{page}\",\"has_more\":{hasMore.ToString().ToLowerInvariant()}}}";
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxBrowserOversizedHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""", Encoding.UTF8, "application/json")
            });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1])
        });
    }
}

sealed class DropboxBrowserAdverseHandler(string mode) : HttpMessageHandler
{
    private int _listingCall;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (mode == "cancel")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
        if (mode == "malformed") return Json("""{"entries":[} """);
        if (mode == "truncated") return Json("{\"entries\":[],\"cursor\":\"truncated\"");
        if (mode == "chunked-oversized")
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[8 * 1024 * 1024 + 1], writable: false))
            };
        if (mode == "terminal-overflow")
        {
            var entries = new StringBuilder(300_000);
            for (var index = 0; index < 2_001; index++)
            {
                if (index != 0) entries.Append(',');
                entries.Append($"{{\".tag\":\"file\",\"name\":\"File {index}\",\"id\":\"id:file-{index}\",\"path_display\":\"/File {index}\"}}");
            }
            return Json($"{{\"entries\":[{entries}],\"cursor\":\"terminal\",\"has_more\":false}}");
        }
        if (mode == "blank-cursor")
            return Json("""{"entries":[],"cursor":" ","has_more":true}""");
        if (mode == "oversized-cursor")
            return Json($"{{\"entries\":[],\"cursor\":\"{new string('c', 16 * 1024 + 1)}\",\"has_more\":true}}");
        if (mode == "repeated-cursor")
        {
            _listingCall++;
            return Json($"{{\"entries\":[{{\".tag\":\"folder\",\"name\":\"Page {_listingCall}\",\"id\":\"id:page-{_listingCall}\",\"path_display\":\"/Page {_listingCall}\"}}],\"cursor\":\"same-cursor\",\"has_more\":true}}");
        }
        if (mode == "cursor-cycle")
        {
            _listingCall++;
            var cursor = _listingCall % 2 == 1 ? "cursor-a" : "cursor-b";
            return Json($"{{\"entries\":[{{\".tag\":\"folder\",\"name\":\"Cycle {_listingCall}\",\"id\":\"id:cycle-{_listingCall}\",\"path_display\":\"/Cycle {_listingCall}\"}}],\"cursor\":\"{cursor}\",\"has_more\":true}}");
        }
        throw new InvalidOperationException("Unexpected adverse browser mode: " + mode);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxRangeContractHandler(string mode = "success") : HttpMessageHandler
{
    public const byte RangeByte = 0x5a;
    private const long TotalBytes = 32L * 1024 * 1024;
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public static PilotRemoteEntry Expected { get; } = new("range.bin", PilotEntryKind.File, "id:range",
        "rev:range", TotalBytes, Hash);

    public int FileMetadataCalls { get; private set; }
    public int EffectCalls { get; private set; }
    public long? RangeFrom { get; private set; }
    public long? RangeTo { get; private set; }
    public long ResponseBodyBytes { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = document.RootElement.GetProperty("path").GetString();
            if (path == "id:pilot-folder")
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            if (path == "id:range")
            {
                FileMetadataCalls++;
                var display = mode == "moved-after-range" && FileMetadataCalls > 1
                    ? "/Outside/range.bin"
                    : "/Pilot/range.bin";
                return Json(FileMetadata(display));
            }
        }
        if (route.EndsWith("/files/download", StringComparison.Ordinal))
        {
            var range = request.Headers.Range?.Ranges.SingleOrDefault()
                        ?? throw new InvalidDataException("The Dropbox range header is absent.");
            RangeFrom = range.From;
            RangeTo = range.To;
            var requested = checked(range.To!.Value - range.From!.Value + 1);
            var responseBytes = mode switch
            {
                "truncated" => requested - 1,
                "excess" => requested + 1,
                _ => requested
            };
            ResponseBodyBytes = responseBytes;
            var response = new HttpResponseMessage(mode == "ignored-range"
                ? HttpStatusCode.OK
                : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Enumerable.Repeat(RangeByte, checked((int)responseBytes)).ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From.Value,
                range.To.Value, TotalBytes);
            response.Headers.TryAddWithoutValidation("Dropbox-API-Result",
                (mode == "pathless"
                    ? FileMetadata(null)
                    : mode == "pathless-mismatch"
                        ? FileMetadata(null, "rev:different")
                        : FileMetadata("/Pilot/range.bin")).Replace("\".tag\":\"file\",", "", StringComparison.Ordinal));
            return response;
        }
        throw new InvalidOperationException("Unexpected Dropbox range route: " + route);
    }

    private static string FileMetadata(string? display, string revision = "rev:range")
    {
        var paths = display is null
            ? string.Empty
            : $",\"path_lower\":\"{display.ToLowerInvariant()}\",\"path_display\":\"{display}\"";
        return $"{{\".tag\":\"file\",\"name\":\"range.bin\",\"id\":\"id:range\",\"rev\":\"{revision}\",\"size\":{TotalBytes},\"content_hash\":\"{Hash}\"{paths}}}";
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxControlNamespaceMarkerHandler(
    string rootPath,
    string markerPath,
    byte[] markerBytes,
    string markerHash,
    string mode) : HttpMessageHandler
{
    public int ControlRootMetadataCalls { get; private set; }
    public int MarkerMetadataCalls { get; private set; }
    public int PairRootMetadataCalls { get; private set; }
    public int DownloadCalls { get; private set; }
    public string? DownloadPath { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = body.RootElement.GetProperty("path").GetString();
            if (path == "id:control-root")
            {
                ControlRootMetadataCalls++;
                return Json($"{{\".tag\":\"folder\",\"name\":\"{namespaceIdFromRoot()}\",\"id\":\"id:control-root\",\"path_lower\":\"{rootPath.ToLowerInvariant()}\",\"path_display\":\"{rootPath}\"}}");
            }
            if (path == "id:pilot-folder")
            {
                PairRootMetadataCalls++;
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            }
            if (path == markerPath)
            {
                MarkerMetadataCalls++;
                var returnedPath = mode == "moved-metadata" ? rootPath + "/moved-owner.json" : markerPath;
                return Json(MarkerMetadata(returnedPath));
            }
        }
        if (route.EndsWith("/files/download", StringComparison.Ordinal))
        {
            DownloadCalls++;
            using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            DownloadPath = argument.RootElement.GetProperty("path").GetString();
            var returnedPath = mode == "moved-download" ? rootPath + "/moved-owner.json" : markerPath;
            var bytes = mode == "corrupt-bytes"
                ? markerBytes.Select((value, index) => index == 0 ? (byte)(value ^ 0xff) : value).ToArray()
                : markerBytes;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Headers.TryAddWithoutValidation("Dropbox-API-Result",
                MarkerMetadata(returnedPath).Replace("\".tag\":\"file\",", "", StringComparison.Ordinal));
            return response;
        }
        throw new InvalidOperationException("Unexpected Dropbox control-namespace route: " + route);
    }

    private string namespaceIdFromRoot() => rootPath.Split('/').Last();

    private string MarkerMetadata(string returnedPath) =>
        $"{{\".tag\":\"file\",\"name\":\".syncshark-owner.json\",\"id\":\"id:control-marker\",\"rev\":\"control-marker\",\"size\":{markerBytes.LongLength},\"content_hash\":\"{markerHash}\",\"path_lower\":\"{returnedPath.ToLowerInvariant()}\",\"path_display\":\"{returnedPath}\"}}";

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class DropboxRecoverySpoolHandler(
    byte[] sourceBytes,
    string sourceHash,
    string mode,
    string spoolRoot,
    TaskCompletionSource<bool>? uploadStarted = null,
    TaskCompletionSource<bool>? releaseUpload = null) : HttpMessageHandler
{
    private int _sourceMetadataCalls;
    public int DownloadCalls { get; private set; }
    public int UploadCalls { get; private set; }
    public bool SpoolObservedDuringUpload { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            return Json("""{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}""");
        if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = body.RootElement.GetProperty("path").GetString();
            if (path == "id:pilot-folder")
                return Json("""{".tag":"folder","name":"Pilot","id":"id:pilot-folder","path_lower":"/pilot","path_display":"/Pilot"}""");
            if (path == "id:large-source")
            {
                _sourceMetadataCalls++;
                var display = mode switch
                {
                    "renamed-before-download" => "/Pilot/renamed-large.bin",
                    "renamed-after-download" when DownloadCalls > 0 => "/Pilot/renamed-large.bin",
                    "moved-before-download" => "/Outside/large.bin",
                    "moved-after-download" when DownloadCalls > 0 => "/Outside/large.bin",
                    _ => "/Pilot/large.bin"
                };
                return Json(SourceMetadata(display));
            }
        }
        if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            return Json("""{"entries":[],"cursor":"complete","has_more":false}""");
        if (route.EndsWith("/files/download", StringComparison.Ordinal))
        {
            DownloadCalls++;
            Stream stream = new MemoryStream(mode == "download-hash-mismatch"
                ? sourceBytes.Select((value, index) => index == 0 ? (byte)(value ^ 0xff) : value).ToArray()
                : sourceBytes, writable: false);
            if (mode == "download-interrupted")
                stream = new InterruptingReadStream(stream, 1024 * 1024);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Headers.TryAddWithoutValidation("Dropbox-API-Result",
                SourceMetadata("/Pilot/large.bin").Replace("\".tag\":\"file\",", "", StringComparison.Ordinal));
            return response;
        }
        if (route.EndsWith("/files/upload", StringComparison.Ordinal))
        {
            UploadCalls++;
            var spools = Directory.GetFiles(spoolRoot, "syncshark-recovery-spool-v1-*.tmp");
            SpoolObservedDuringUpload = spools.Length == 1 &&
                                        new FileInfo(spools[0]).Length == sourceBytes.LongLength;
            uploadStarted?.TrySetResult(true);
            if (mode == "process-crash-during-upload") NativeTestFiles.TerminateCurrentProcess(213);
            if (releaseUpload is not null)
                await releaseUpload.Task.WaitAsync(cancellationToken);
            await using var content = await request.Content!.ReadAsStreamAsync(cancellationToken);
            var uploadedHash = await DropboxContentHash.ComputeAsync(content, cancellationToken);
            if (mode == "upload-rejected")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("simulated post-transmission failure")
                };
            if (mode == "malformed-upload-ack") return Json("{}");
            return Json($"{{\".tag\":\"file\",\"name\":\"retained.bin\",\"id\":\"id:retained\",\"rev\":\"rev:retained\",\"size\":{sourceBytes.LongLength},\"content_hash\":\"{uploadedHash}\"}}");
        }
        throw new InvalidOperationException("Unexpected recovery spool route: " + route);
    }

    private string SourceMetadata(string display) =>
        $"{{\".tag\":\"file\",\"name\":\"large.bin\",\"id\":\"id:large-source\",\"rev\":\"rev:large-source\",\"size\":{sourceBytes.LongLength},\"content_hash\":\"{sourceHash}\",\"path_lower\":\"{display.ToLowerInvariant()}\",\"path_display\":\"{display}\"}}";

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}

sealed class InterruptingReadStream(Stream inner, long failAfter) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_read >= failAfter) throw new IOException("simulated interrupted recovery download");
        var read = inner.Read(buffer[..checked((int)Math.Min(buffer.Length, failAfter - _read))]);
        _read += read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_read >= failAfter) throw new IOException("simulated interrupted recovery download");
        var read = await inner.ReadAsync(buffer[..checked((int)Math.Min(buffer.Length, failAfter - _read))],
            cancellationToken);
        _read += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

static class TestScale
{
    public const int StressEntryCount = 100;
}

sealed class IsolatedTestProductRuntime : IDisposable
{
    private readonly bool _ownsRoot;
    private readonly IDisposable _override;
    private bool _disposed;

    public IsolatedTestProductRuntime(string? inheritedRoot)
    {
        _ownsRoot = inheritedRoot is null;
        Root = inheritedRoot ?? Path.Combine(Path.GetTempPath(), "sync-shark-pilot-tests",
            "product-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _override = SyncSharkRuntimePaths.OverrideProductRootForTest(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _override.Dispose();
        if (_ownsRoot && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
