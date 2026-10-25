using System.Text.Json;

internal sealed record TrueSyncCaseRequirement(
    string CaseId,
    string Category,
    string Title,
    string Preconditions,
    IReadOnlyList<string> Identities,
    string PairMemberships,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> AllowedEffects,
    IReadOnlyList<string> ForbiddenEffects,
    IReadOnlyList<string> ExpectedDatabaseTransitions,
    string ExpectedUserResult,
    string RestartOutcome,
    IReadOnlyList<string> TestIds);

internal sealed record TrueSyncBehaviorProfile(
    string ProfileId,
    string ExpectedUserResult,
    IReadOnlyList<string> AllowedEffects,
    IReadOnlyList<string> ExpectedDatabaseTransitions,
    string RestartOutcome,
    IReadOnlyList<string> ImplementationSources,
    IReadOnlyList<string> ModelWitnesses,
    IReadOnlyList<string> CrashWitnesses,
    IReadOnlyList<string> UiWitnesses);

internal sealed record TrueSyncCaseWitnessMapping(
    string CaseId,
    string Category,
    string Title,
    string ProfileId,
    IReadOnlyList<string> ImplementationSources,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TestWitnesses);

internal sealed record TrueSyncWitnessRegistryResult(
    IReadOnlyList<TrueSyncCaseWitnessMapping> Mappings,
    IReadOnlyList<string> Missing,
    IReadOnlyDictionary<string, int> ProfileCounts);

internal sealed record TrueSyncWitnessDependencyMapping(
    string TestId,
    string CaseId,
    string ProfileId,
    string Kind,
    IReadOnlyList<string> ProductionWitnesses,
    IReadOnlyList<string> ExecutionPhases,
    bool DependenciesSatisfied);

internal sealed record TrueSyncWitnessDependencyResult(
    IReadOnlyList<TrueSyncWitnessDependencyMapping> Mappings,
    IReadOnlyList<string> Missing);

internal static class TrueSyncWitnessRegistry
{
    private static readonly IReadOnlyList<string> NoEffectUnlessPreconditions =
        ["no external effect unless every operation-specific precondition is satisfied"];

    private static readonly IReadOnlyList<string> RequiredForbiddenEffects =
    [
        "path-only overwrite or deletion",
        "clearing or overwriting another unresolved operation",
        "adopting unknown product artifacts or namespaces"
    ];

    private static readonly IReadOnlyList<TrueSyncBehaviorProfile> Profiles =
    [
        Profile(
            "authority-projection",
            "Record the current truth in SQLite and expose it without allowing UI state to become authority.",
            NoEffectUnlessPreconditions,
            ["authoritative rows committed", "projection refreshed"],
            "UI reconstructs from authority.",
            ["src/SyncShark.Core/SyncAuthorityDatabase.cs", "src/SyncShark.Core/SyncAuthorityRuntimeHost.cs"],
            ["CloudProAuthorityPreflight.AssertFrozenPlansAsync"],
            ["CloudProAuthorityPreflight.AssertFrozenPlansAsync"],
            ["CloudProAuthorityPreflight.AssertPairRunnerAsync"]),
        Profile(
            "database-integrity-recovery",
            "Preserve the last committed authority, fail closed on integrity uncertainty, and resume leases/operations from durable rows.",
            NoEffectUnlessPreconditions,
            ["SQLite transaction is atomic", "state transition checked against transition table", "integrity/lease outcome recorded"],
            "Startup recovery performs database checks, expires stale leases, and resumes unresolved operations before normal planning.",
            ["src/SyncShark.Core/SyncAuthorityDatabase.cs", "src/SyncShark.Core/SyncAuthoritySchema.cs", "src/SyncShark.Core/SyncLocalTransactionDirectory.cs"],
            ["CloudProAuthorityPreflight.AssertRuntimeAsync", "CloudProAuthorityPreflight.AssertSchemaInventoryAsync", "CloudProAuthorityPreflight.AssertInitializationAndStorageFailuresAsync"],
            ["CloudProAuthorityPreflight.AssertInitializationAndStorageFailuresAsync", "CloudProAuthorityPreflight.AssertWalCrashRecoveryAsync", "CloudProAuthorityPreflight.AssertFreshCreateCrashBoundariesAsync"],
            ["CloudProAuthorityPreflight.AssertPairRunnerAsync"]),
        Profile(
            "recoverable-deletion",
            "Represent absence as a tombstone first. Propagate only after the peer is proven unchanged and recoverability and authority are revalidated.",
            ["create verified recovery evidence", "conditional source retirement only after effect-time membership and identity proof"],
            ["tombstone inserted/updated", "operation and recovery allocation committed", "verified retirement commits new synchronization point"],
            "Recovery reconciles recovery evidence and source membership; it never equates absence with successful deletion.",
            ["src/SyncShark.Core/SyncAuthorityModels.cs", "src/SyncShark.Core/SyncAuthorityReconciliationPlanner.cs", "src/SyncShark.Core/SyncAuthorityPairRunner.cs", "src/SyncShark.Core/SyncAuthorityDeleteLocalExecutor.cs", "src/SyncShark.Core/SyncAuthorityDeleteRemoteExecutor.cs", "src/SyncShark.Core/SyncAuthorityDeleteLocalReconciler.cs", "src/SyncShark.Core/SyncAuthorityDeleteRemoteReconciler.cs"],
            ["RecoverableDeletionPropagation", "RecoverySourceAbsenceIsNotProof", "CloudProAuthorityPreflight.AssertPairRunnerAsync"],
            ["RecoverableDeletionArchiveUncertain", "TrueSyncProcessCrashRecovery", "CloudProAuthorityPreflight.AssertPairRunnerAsync"],
            ["RecoverableDeletionPropagation", "CloudProAuthorityPreflight.AssertPairRunnerAsync"]),
        Profile(
            "identity-aware-movement",
            "Converge as an identity-aware move when continuity is proven; otherwise use conservative creation plus an unresolved tombstone.",
            ["destination create/rename after identity and membership proof", "source retirement after destination verification"],
            ["source/destination observations committed", "movement operation + both memberships committed atomically", "destination verified before source retirement"],
            "The operation resumes from its durable phase and cannot retire a source whose current membership is unproved.",
            ["src/SyncShark.Core/SyncAuthorityCrossPairMoveCoordinator.cs", "src/SyncShark.Core/SyncAuthorityMoveLocalExecutor.cs", "src/SyncShark.Core/SyncAuthorityMoveRemoteExecutor.cs", "src/SyncShark.Core/SyncAuthorityMoveLocalReconciler.cs", "src/SyncShark.Core/SyncAuthorityMoveRemoteReconciler.cs"],
            ["CloudProAuthorityPreflight.AssertCrossPairMoveAsync", "RecoverableRenamePropagation"],
            ["TrueSyncProcessCrashRecovery"],
            ["RecoverableRenamePropagation"]),
        Profile(
            "blocked-validation",
            "Fail closed with a precise Blocked state; preserve every user object and issue no external mutation.",
            NoEffectUnlessPreconditions,
            ["observation/validation recorded", "blocked_reason persisted", "history projection updated"],
            "Restart repeats validation; it cannot silently adopt, delete, overwrite, or clear the block.",
            ["src/SyncShark.Core/SyncAuthorityReconciliationPlanner.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["CloudProAuthorityPreflight.AssertCurrentObservationIdentityResolutionAsync"],
            ["CloudProAuthorityPreflight.AssertFencedAttemptsAndReconciliationAsync"],
            ["CloudProAuthorityPreflight.AssertPairRunnerAsync"]),
        Profile(
            "durable-monitor-hint",
            "Persist the event/cursor as a hint, mark bounded dirty scope, and let reconciliation determine authoritative work.",
            NoEffectUnlessPreconditions,
            ["monitor_event and cursor transaction committed", "dirty_item upserted", "planner runs only after observation commit"],
            "Duplicate and missing hints are harmless because reconciliation and cursors are durable.",
            ["src/SyncShark.Core/SyncAuthorityLocalMonitor.cs", "src/SyncShark.Core/SyncAuthorityDropboxMonitor.cs", "src/SyncShark.Core/SyncAuthorityObservationCoordinator.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["CloudProAuthorityPreflight.AssertMonitorBatchAtomicityAsync"],
            ["CloudProAuthorityPreflight.AssertPairRunnerAsync"],
            ["CloudProAuthorityPreflight.AssertMonitorBatchAtomicityAsync"]),
        Profile(
            "identity-bound-mutation",
            "Create one durable operation for the exact object/version, stage privately, revalidate, effect, verify, and commit a full-identity synchronization point.",
            ["content staging/acquisition", "revision-conditional Dropbox update", "handle-bound local promotion"],
            ["operation + attempt + preconditions committed", "effect receipt recorded as known or unknown", "verified object_version and synchronization_point committed", "cleanup remains separate"],
            "An effect-unknown attempt is reconciled by inventory; it is never blindly repeated or replaced by another path.",
            ["src/SyncShark.Core/SyncAuthorityUpdateLocalExecutor.cs", "src/SyncShark.Core/SyncAuthorityUpdateRemoteExecutor.cs", "src/SyncShark.Core/SyncAuthorityUpdateLocalReconciler.cs", "src/SyncShark.Core/SyncAuthorityUpdateRemoteReconciler.cs"],
            ["BidirectionalModifications", "SameByteLocalIdentityReplacementConflicts"],
            ["LocalRecoveryResponseLoss", "TrueSyncProcessCrashRecovery"],
            ["BidirectionalModifications"]),
        Profile(
            "pause-and-capability-boundary",
            "Expose the exact operational phase. Monitoring may continue, but no new effect begins while paused or disabled.",
            NoEffectUnlessPreconditions,
            ["configuration/lease state recorded", "ready work retained", "no authority row removed"],
            "Queue and pause state are reconstructed from SQLite, not from the prior UI process.",
            ["src/SyncShark.Core/SyncAuthorityRuntimeHost.cs", "src/SyncShark.Core/SyncAuthorityPairRunner.cs", "src/SyncShark.Core/SyncCapabilityGate.cs"],
            ["CloudProAuthorityPreflight.AssertPairRunnerAsync", "PairPolicyAndRunFence"],
            ["CloudProAuthorityPreflight.AssertFencedAttemptsAndReconciliationAsync"],
            ["PairPolicyAndRunFence"]),
        Profile(
            "immutable-conflict-generation",
            "Create or refresh an immutable conflict generation, preserve both versions, and require an explicit current-generation resolution.",
            NoEffectUnlessPreconditions,
            ["observations committed", "conflict + conflict_version rows inserted", "memberships remain unchanged"],
            "Conflict remains durable and resolution-safe; stale choices are rejected.",
            ["src/SyncShark.Core/SyncAuthorityConflictResolutionCoordinator.cs", "src/SyncShark.Core/SyncAuthorityPreserveConflictCopyExecutor.cs", "src/SyncShark.Core/SyncAuthorityPreserveConflictCopyReconciler.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["BidirectionalConflict", "RecoverableConflictResolution"],
            ["CompositeConflictRecovery", "TrueSyncProcessCrashRecovery"],
            ["RecoverableConflictResolution"]),
        Profile(
            "dropbox-observation-and-uncertainty",
            "Treat provider metadata as an observation, preserve request/response uncertainty, and require exact account, namespace, ID, revision, hash, and membership proof appropriate to the effect.",
            NoEffectUnlessPreconditions,
            ["attempt request fingerprint committed", "provider result or uncertainty receipt appended", "reconciliation observation committed"],
            "Provider state is re-read; ambiguous responses do not become success by assertion.",
            ["src/SyncShark.Core/DropboxProvider.cs", "src/SyncShark.Core/SyncAuthorityObservationCoordinator.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["DropboxUploadWireContract", "DropboxUpdateWireContract", "DropboxDownloadWireContract", "DropboxArchiveWireContract", "CloudProAuthorityPreflight.AssertConditionalDeleteFailureClassificationAsync"],
            ["BidirectionalUploadRecovery", "BidirectionalUploadNoEffectRecovery", "CloudProAuthorityPreflight.AssertConditionalDeleteFailureClassificationAsync"],
            ["DropboxUploadWireContract"]),
        Profile(
            "out-of-scope-retention",
            "Mark the former membership out of scope or ambiguous. Never follow the stale identity and never delete the moved object.",
            NoEffectUnlessPreconditions,
            ["latest observation committed", "membership becomes out_of_scope or absent_unconfirmed", "dependent destructive work blocked/superseded"],
            "A fresh reconciliation re-proves scope before any effect; absence alone remains non-destructive.",
            ["src/SyncShark.Core/SyncAuthorityReconciliationPlanner.cs", "src/SyncShark.Core/SyncAuthorityDeleteRemoteReconciler.cs", "src/SyncShark.Core/SyncAuthorityMoveRemoteReconciler.cs"],
            ["CloudProAuthorityPreflight.AssertCurrentObservationIdentityResolutionAsync"],
            ["RecoverableDeletionArchiveUncertain", "RecoverySourceAbsenceIsNotProof"],
            ["RecoverySourceAbsenceIsNotProof"]),
        Profile(
            "effect-free-initial-discovery",
            "Perform effect-free identity-bound observations. Adopt a baseline only after the configured stability proof; divergent same paths become conflicts.",
            NoEffectUnlessPreconditions,
            ["pair/root binding committed", "scan generations committed", "synchronization points inserted only after stable equivalence/adoption"],
            "Incomplete discovery resumes with another observation and no copy, overwrite, move, or delete.",
            ["src/SyncShark.Core/SyncAuthorityObservationCoordinator.cs", "src/SyncShark.Core/SyncAuthorityInitialPlanner.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["CloudProAuthorityPreflight.AssertEffectFreeObservationsAsync", "CloudProAuthorityPreflight.AssertUncertainInitialDiscoveryAsync", "CloudProAuthorityPreflight.AssertObservationCoordinatorAsync"],
            ["AggregateMigrationRecovery"],
            ["BidirectionalBaseline"]),
        Profile(
            "security-authority-rejection",
            "Reject the effect and retain evidence without leaking credentials or user contents.",
            NoEffectUnlessPreconditions,
            ["authority failure recorded", "operation blocked", "redacted diagnostic/history projection written"],
            "The same authority checks run again before any effect.",
            ["src/SyncShark.Core/SyncCapabilityGate.cs", "src/SyncShark.Core/SyncControlNamespace.cs", "src/SyncShark.Core/SyncAuthorityDatabase.cs"],
            ["CloudProAuthorityPreflight.AssertNamespaceAllocationAuthorityAsync", "CloudProAuthorityPreflight.AssertAtomicPathReservationAsync"],
            ["CloudProAuthorityPreflight.AssertControlAllocationCrashRecoveryAsync"],
            ["CloudProAuthorityPreflight.AssertNamespaceAllocationAuthorityAsync"])
    ];

    public static TrueSyncWitnessRegistryResult Build(
        IReadOnlyList<TrueSyncCaseRequirement> requirements,
        string repositoryRoot)
    {
        var findings = new List<string>();
        var profilesByResult = Profiles.ToDictionary(profile => profile.ExpectedUserResult, StringComparer.Ordinal);
        var profileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mappings = new List<TrueSyncCaseWitnessMapping>(requirements.Count);

        foreach (var requirement in requirements)
        {
            if (!profilesByResult.TryGetValue(requirement.ExpectedUserResult, out var profile))
            {
                findings.Add(requirement.CaseId + ": no behavior profile matches expected_user_result");
                continue;
            }

            profileCounts[profile.ProfileId] = profileCounts.GetValueOrDefault(profile.ProfileId) + 1;
            var witnesses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var testId in requirement.TestIds)
            {
                var kind = testId.EndsWith("-MODEL", StringComparison.Ordinal) ? "model" :
                    testId.EndsWith("-CRASH", StringComparison.Ordinal) ? "crash" :
                    testId.EndsWith("-UI", StringComparison.Ordinal) ? "ui" : "unknown";
                var values = kind switch
                {
                    "model" => profile.ModelWitnesses,
                    "crash" => profile.CrashWitnesses,
                    "ui" => profile.UiWitnesses,
                    _ => Array.Empty<string>()
                };
                if (values.Count == 0) findings.Add(testId + ": no concrete witness owner");
                witnesses[testId] = values;
            }

            mappings.Add(new TrueSyncCaseWitnessMapping(requirement.CaseId, requirement.Category,
                requirement.Title, profile.ProfileId, profile.ImplementationSources, witnesses));
        }

        foreach (var profile in Profiles)
        {
            foreach (var relativePath in profile.ImplementationSources)
            {
                if (!File.Exists(Path.Combine(repositoryRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar))))
                    findings.Add(profile.ProfileId + ": implementation source is absent: " + relativePath);
            }

            foreach (var witness in profile.ModelWitnesses.Concat(profile.CrashWitnesses).Concat(profile.UiWitnesses))
            {
                var symbol = witness[(witness.LastIndexOf('.') + 1)..];
                if (!RepositoryContainsSymbol(repositoryRoot, symbol))
                    findings.Add(profile.ProfileId + ": witness symbol is absent: " + witness);
            }
        }

        if (mappings.Count != requirements.Count)
            findings.Add($"case mapping mismatch: expected={requirements.Count}, mapped={mappings.Count}");

        return new TrueSyncWitnessRegistryResult(mappings, findings, profileCounts);
    }

    public static TrueSyncWitnessDependencyResult ValidateDependencies(
        IReadOnlyList<TrueSyncCaseRequirement> requirements,
        TrueSyncWitnessRegistryResult registry,
        IReadOnlyDictionary<string, bool> phaseResults)
    {
        var missing = new List<string>();
        var mappings = new List<TrueSyncWitnessDependencyMapping>(requirements.Count * 3);
        var profileById = Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);
        var mappingByCase = registry.Mappings.ToDictionary(mapping => mapping.CaseId, StringComparer.Ordinal);

        foreach (var requirement in requirements)
        {
            if (!mappingByCase.TryGetValue(requirement.CaseId, out var mapping) ||
                !profileById.TryGetValue(mapping.ProfileId, out var profile))
            {
                missing.Add(requirement.CaseId + ": generated witness mapping is unavailable");
                continue;
            }

            var commonValid = !string.IsNullOrWhiteSpace(requirement.Preconditions) &&
                              requirement.Preconditions.Contains("unknown, not as permission", StringComparison.Ordinal) &&
                              requirement.Identities.Count >= 3 &&
                              requirement.PairMemberships.Contains("re-proved", StringComparison.Ordinal) &&
                              requirement.Events.Count != 0 &&
                              RequiredForbiddenEffects.SequenceEqual(requirement.ForbiddenEffects, StringComparer.Ordinal);
            var modelValid = commonValid &&
                             profile.AllowedEffects.SequenceEqual(requirement.AllowedEffects, StringComparer.Ordinal) &&
                             profile.ExpectedDatabaseTransitions.SequenceEqual(
                                 requirement.ExpectedDatabaseTransitions, StringComparer.Ordinal);
            var crashValid = commonValid &&
                             string.Equals(profile.RestartOutcome, requirement.RestartOutcome,
                                 StringComparison.Ordinal);
            var uiValid = commonValid &&
                          string.Equals(profile.ExpectedUserResult, requirement.ExpectedUserResult,
                              StringComparison.Ordinal);

            foreach (var testId in requirement.TestIds)
            {
                var kind = testId.EndsWith("-MODEL", StringComparison.Ordinal) ? "model" :
                    testId.EndsWith("-CRASH", StringComparison.Ordinal) ? "crash" :
                    testId.EndsWith("-UI", StringComparison.Ordinal) ? "ui" : "unknown";
                var passed = kind switch
                {
                    "model" => modelValid,
                    "crash" => crashValid,
                    "ui" => uiValid,
                    _ => false
                };
                var productionWitnesses = mapping.TestWitnesses.GetValueOrDefault(testId) ?? [];
                var executionPhases = productionWitnesses.Select(ExecutionPhaseForWitness)
                    .Distinct(StringComparer.Ordinal).ToArray();
                passed &= productionWitnesses.Count != 0 && executionPhases.Length != 0 &&
                          executionPhases.All(phase => phaseResults.GetValueOrDefault(phase));
                mappings.Add(new TrueSyncWitnessDependencyMapping(testId, requirement.CaseId, profile.ProfileId, kind,
                    productionWitnesses, executionPhases, passed));
                if (!passed) missing.Add(testId + ": mapped production-witness dependency is not satisfied");
            }
        }

        var expectedIds = requirements.SelectMany(requirement => requirement.TestIds)
            .ToHashSet(StringComparer.Ordinal);
        var actualIds = mappings.Select(item => item.TestId).ToHashSet(StringComparer.Ordinal);
        missing.AddRange(expectedIds.Except(actualIds).Select(testId => testId + ": not mapped"));
        missing.AddRange(actualIds.Except(expectedIds).Select(testId => testId + ": unexpected mapping"));
        if (mappings.Count != requirements.Count * 3)
            missing.Add($"generated triad mapping mismatch: expected={requirements.Count * 3}, actual={mappings.Count}");
        return new TrueSyncWitnessDependencyResult(mappings, missing);
    }

    public static async Task WriteManifestAsync(string path, string contractHash,
        TrueSyncWitnessRegistryResult registry, TrueSyncWitnessDependencyResult dependencies)
    {
        var manifest = new
        {
            contract_sha256 = contractHash,
            generated_utc = DateTimeOffset.UtcNow,
            implementation_owner = "Sync Shark product owner",
            coverage_kind = "structural generated-case to production-witness dependency mapping; not case-specific execution",
            cases_mapped = registry.Mappings.Count,
            generated_test_ids_declared = dependencies.Mappings.Count,
            generated_test_ids_executed = 0,
            case_specific_execution_claim = false,
            profile_counts = registry.ProfileCounts,
            missing = registry.Missing.Concat(dependencies.Missing).ToArray(),
            dependency_mappings = dependencies.Mappings,
            requirements = registry.Mappings
        };
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static TrueSyncBehaviorProfile Profile(string profileId, string expectedUserResult,
        IReadOnlyList<string> allowedEffects, IReadOnlyList<string> expectedDatabaseTransitions,
        string restartOutcome, IReadOnlyList<string> sources, IReadOnlyList<string> model,
        IReadOnlyList<string> crash, IReadOnlyList<string> ui) => new(profileId, expectedUserResult,
        allowedEffects, expectedDatabaseTransitions, restartOutcome, sources, model, crash, ui);

    private static bool RepositoryContainsSymbol(string repositoryRoot, string symbol)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs",
                     SearchOption.AllDirectories)
                 .Concat(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "tests"), "*.cs",
                     SearchOption.AllDirectories)))
        {
            if (File.ReadAllText(path).Contains(symbol, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string ExecutionPhaseForWitness(string witness)
    {
        if (witness.StartsWith("CloudProAuthorityPreflight.", StringComparison.Ordinal))
            return "sqlite-authority";
        if (string.Equals(witness, "TrueSyncProcessCrashRecovery", StringComparison.Ordinal))
            return "true-sync-process-crash";
        return "complete-test-registry";
    }
}
