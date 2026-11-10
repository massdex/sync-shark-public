using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// E3 endpoint runtime contract.
//
// This is the runtime half of the frozen E1 endpoint contract: it resolves a pair's persisted left/right
// endpoints, enforces the endpoint, pair and cross-pair rules before any effect is admitted, rebinds the
// pair's right side to its real local endpoint adapter and executes through the existing authority,
// planning, journal and recovery pipeline.

/// <summary>A runtime refusal that must stop the pair before any effect is admitted.</summary>
public sealed class SyncEndpointRuntimeRefusedException(SyncEndpointValidation validation)
    : InvalidOperationException("The pair endpoints were refused before any effect: " + validation.Describe())
{
    public SyncEndpointValidation Validation { get; } = validation;

    public IReadOnlyList<SyncEndpointError> Errors => Validation.Errors;
}

/// <summary>One resolved pair: its endpoints, their validation and the local roots they bind.</summary>
public sealed record SyncEndpointPairResolution(
    SyncPair Pair,
    SyncPairEndpoints? Endpoints,
    SyncEndpointValidation Validation,
    SyncLocalEndpointBinding? LeftLocal,
    SyncLocalEndpointBinding? RightLocal)
{
    public bool IsResolved => Endpoints is not null && Validation.IsValid;

    public bool IsLocalToLocal => IsResolved && Endpoints!.Left.IsLocal && Endpoints.Right.IsLocal;
}

/// <summary>The installation identity and the bound recovery container one local/local run uses.</summary>
public sealed record SyncLocalPairRuntimeOptions(
    string InstallationId,
    string ProfileId,
    string CapabilityProfileJson,
    
    int RecoveryRetentionDays = 30,
    bool EnableDeletionEffects = false,
    bool EnableMoveEffects = false,
    int MaximumPlanningPasses = 4);

/// <summary>Prepared bindings for the same runner used by scheduled, manual and coordinated runs.</summary>
public sealed record SyncPreparedLocalPair(SyncPair Pair, ISyncRemoteStore Remote,
    string LeftRootBindingId, string RightRootBindingId, SyncAuthorityPlannerOptions PlannerOptions)
{
    internal SyncAuthorityDatabase.LocalPairReplacementRoots? ReplacementRoots { get; init; }
}

public static class SyncEndpointPairRuntime
{
    /// <summary>One runtime capability decision shared by authoring surfaces and the binding host.</summary>
    public static string? BindingSupportReason(SyncEndpointKind left, SyncEndpointKind right)
    {
        _ = SyncEndpointProviders.Get(left);
        _ = SyncEndpointProviders.Get(right);
        return null;
    }

    /// <summary>
    /// The provider literal of a pair whose right side is a real local endpoint. It selects the
    /// provider-scoped recovery rules (like the existing Google Drive exception) while keeping the
    /// pair's content-identity algorithm unchanged.
    /// </summary>
    public const string LocalProviderLiteral = SyncProviders.Local;

    internal static string RightControlNamespaceId(string pairId) => Id("right-control", pairId);

    /// <summary>
    /// Resolves and validates one pair's endpoints. A legacy pair with no endpoint document maps its
    /// saved fields; an explicit document is validated record by record, then as a pair, then against
    /// every retained pair so an identical or containing endpoint can never be admitted twice.
    /// </summary>
    public static SyncEndpointPairResolution Resolve(SyncPair pair, SyncEndpointDocument? document,
        IEnumerable<SyncPairEndpoints>? retainedPairs = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        document ??= pair.EndpointDocument;
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
        {
            var cloudValidation = document == pair.EndpointDocument
                ? SyncCloudPairConfiguration.ValidateProjection(pair)
                : new SyncEndpointValidation(new[]
                {
                    new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                        "The supplied endpoint document is not this cloud pair's retained document.")
                });
            var cloudEndpoints = cloudValidation.IsValid ? SyncCloudPairConfiguration.RequireProjection(pair) : null;
            if (cloudEndpoints is not null && retainedPairs is not null)
                cloudValidation = SyncCloudPairConfiguration.ValidateAgainstRetainedPairs(cloudEndpoints, retainedPairs);
            return new SyncEndpointPairResolution(pair, cloudValidation.IsValid ? cloudEndpoints : null,
                cloudValidation, null, null);
        }
        SyncPairEndpoints? endpoints = null;
        var refusals = new List<SyncEndpointRefusal>();
        if (document is null)
        {
            var legacy = SyncEndpointsCompatibility.FromSyncPair(pair);
            endpoints = legacy.Endpoints;
            refusals.AddRange(legacy.Validation.Refusals);
        }
        else if (document.Version != SyncEndpointSchema.SymmetricEndpoints)
        {
            refusals.Add(new(SyncEndpointError.UnsupportedSchemaVersion, "The endpoint document version is not supported."));
        }
        else
        {
            SyncEndpoint? left = null;
            SyncEndpoint? right = null;
            if (document.Left is null)
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                    "The saved endpoint document has no left endpoint."));
            else if (SyncEndpointContracts.ValidateRecord(document.Left) is { IsValid: false } leftRefusal)
                refusals.AddRange(leftRefusal.Refusals);
            else
                left = SyncEndpointContracts.ToEndpoint(document.Left);
            if (document.Right is null)
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                    "The saved endpoint document has no right endpoint."));
            else if (SyncEndpointContracts.ValidateRecord(document.Right) is { IsValid: false } rightRefusal)
                refusals.AddRange(rightRefusal.Refusals);
            else
                right = SyncEndpointContracts.ToEndpoint(document.Right);
            if (left is not null && right is not null)
            {
                refusals.AddRange(SyncEndpointContracts.ValidatePair(left, right).Refusals);
                if (left.IsLocal || right.IsLocal)
                    refusals.AddRange(RequireLocalAgreesWithPair(pair, left.IsLocal ? left : right).Refusals);
            }
            if (refusals.Count == 0 && left is not null && right is not null)
                endpoints = new SyncPairEndpoints(left, right, pair.Direction);
        }
        var validation = Conclude(refusals);
        if (validation.IsValid && endpoints is not null && retainedPairs is not null)
            validation = SyncEndpointContracts.ValidatePairAgainstExisting(endpoints.Left, endpoints.Right,
                retainedPairs);
        var resolved = validation.IsValid ? endpoints : null;
        return new SyncEndpointPairResolution(pair, resolved, validation,
            resolved?.Left.IsLocal == true ? LocalBinding(resolved.Left) : null,
            resolved?.Right.IsLocal == true ? LocalBinding(resolved.Right) : null);
    }

    /// <summary>
    /// The full-pair runtime resolution: the intra-pair checks run exactly as in Resolve, and the cross-pair
    /// rule is the one shared partition policy over complete retained PAIR definitions, so an otherwise-proven
    /// nested partition is admitted while an equal, unexcluded-nested or otherwise refused relation is not.
    /// The strict endpoint-only overload above is unchanged and remains available to raw callers.
    /// </summary>
    public static SyncEndpointPairResolution ResolveAgainstRetainedPairs(SyncPair pair,
        SyncEndpointDocument? document, IEnumerable<SyncPair>? retainedPairs)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var resolution = Resolve(pair, document);
        if (!resolution.IsResolved || retainedPairs is null) return resolution;
        // A strict runtime entry point must not accept an Unverified relation: the established overlap/identity
        // check is never waived by an unresolved or malformed retained pair.
        var validation = SyncPairPartitionPolicy.ValidateAgainstRetainedPairs(pair, retainedPairs,
            refuseUnverified: true);
        if (validation.IsValid) return resolution;
        return resolution with
        {
            Endpoints = null,
            Validation = validation,
            LeftLocal = null,
            RightLocal = null
        };
    }

    /// <summary>
    /// The physical local endpoint must match the pair's held local port regardless of which side it
    /// occupies. A different folder is a rebind, not a runtime detail.
    /// </summary>
    public static SyncEndpointValidation RequireLocalAgreesWithPair(SyncPair pair, SyncEndpoint left)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(left);
        if (!left.IsLocal) return SyncEndpointValidation.Accepted;
        var refusals = new List<SyncEndpointRefusal>();
        if (!string.Equals(PilotPath.NormalizeLocalRoot(left.LocalPath!),
                PilotPath.NormalizeLocalRoot(pair.LocalRoot), StringComparison.OrdinalIgnoreCase))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved local endpoint is not this pair's own local root; an endpoint identity change is an explicit rebind."));
        if (left.LocalIdentity is { } identity &&
            (identity.VolumeSerialNumber != pair.LocalVolumeSerialNumber ||
             identity.RootFileId != pair.LocalRootFileId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidLocalIdentity,
                "The saved local endpoint identity is not this pair's own local root identity."));
        return Conclude(refusals);
    }

    /// <summary>
    /// Reopens both local endpoints by their held identity. A moved, replaced or reparse-point root
    /// refuses here, before registration, planning or any effect.
    /// </summary>
    public static void VerifyLocalRoots(SyncEndpointPairResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.IsResolved)
            throw new SyncEndpointRuntimeRefusedException(resolution.Validation);
        foreach (var binding in new[] { resolution.LeftLocal, resolution.RightLocal })
        {
            if (binding is null) continue;
            using var root = PilotDirectoryLease.Open(binding.RootPath, binding.Identity);
            root.VerifyBinding();
        }
    }

    /// <summary>Builds the real local endpoint adapter for a resolved right local endpoint.</summary>
    public static SyncLocalEndpointStore CreateRightStore(SyncEndpointPairResolution resolution,
        SyncControlNamespaceBinding control)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.IsResolved)
            throw new SyncEndpointRuntimeRefusedException(resolution.Validation);
        if (resolution.RightLocal is null)
            throw new SyncEndpointRuntimeRefusedException(Conclude(new List<SyncEndpointRefusal>
            {
                new(SyncEndpointError.EndpointKindMismatch,
                    "The right endpoint is not a local endpoint; a local adapter was requested for it.")
            }));
        return new SyncLocalEndpointStore(resolution.RightLocal, control, LocalProviderLiteral);
    }

    /// <summary>
    /// Verifies and registers two real local endpoints for the existing authority pipeline. Returning
    /// preparation separately lets all host run modes share the same identity and recovery checks.
    /// This allocates private control metadata; it does not transfer or delete user files.
    /// </summary>
    public static Task<SyncPreparedLocalPair> PrepareLocalToLocalAsync(SyncPair pair, SyncEndpointDocument document,
        SyncAuthorityDatabase authority, SyncLocalPairRuntimeOptions options,
        IReadOnlyList<SyncPairEndpoints>? retainedPairs = null, PilotRunControl? runControl = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<SyncPair>? retainedPairDefinitions = null) =>
        PrepareLocalToLocalCoreAsync(pair, document, authority, options, retainedPairs, runControl, null,
            cancellationToken, retainedPairDefinitions);

    internal static Task<SyncPreparedLocalPair> PrepareLocalToLocalReplacementAsync(SyncPair pair,
        SyncEndpointDocument document, SyncAuthorityDatabase authority, SyncLocalPairRuntimeOptions options,
        SyncPair replacedPair, CancellationToken cancellationToken) =>
        PrepareLocalToLocalCoreAsync(pair, document, authority, options, null, null, replacedPair, cancellationToken);

    private static async Task<SyncPreparedLocalPair> PrepareLocalToLocalCoreAsync(SyncPair pair,
        SyncEndpointDocument document, SyncAuthorityDatabase authority, SyncLocalPairRuntimeOptions options,
        IReadOnlyList<SyncPairEndpoints>? retainedPairs, PilotRunControl? runControl, SyncPair? replacedPair,
        CancellationToken cancellationToken, IReadOnlyList<SyncPair>? retainedPairDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local endpoints require Windows NTFS identities.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstallationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CapabilityProfileJson);
        var activeControl = runControl ?? new PilotRunControl();
        await activeControl.WaitBeforeNextEffectAsync(cancellationToken);
        // The full-pair retained-pairs path is the shared partition policy; the raw endpoint-list path stays
        // strict for callers that only hold endpoints.
        var resolution = retainedPairDefinitions is not null
            ? ResolveAgainstRetainedPairs(pair, document, retainedPairDefinitions)
            : Resolve(pair, document, retainedPairs);
        if (!resolution.IsResolved)
            throw new SyncEndpointRuntimeRefusedException(resolution.Validation);
        if (!resolution.IsLocalToLocal)
            throw new SyncEndpointRuntimeRefusedException(Conclude(new List<SyncEndpointRefusal>
            {
                new(SyncEndpointError.EndpointKindMismatch,
                    "This runtime slice requires two local endpoints; the resolved pair is not local-to-local.")
            }));
        var left = resolution.LeftLocal!;
        var right = resolution.RightLocal!;
        VerifyLocalRoots(resolution);
        var runPair = pair with
        {
            Provider = LocalProviderLiteral,
            DropboxRoot = right.RootPath,
            DropboxFolderId = right.IdentityToken,
            RecoveryRoot = null,
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right
        };
        await authority.InitializeAsync(options.InstallationId, options.ProfileId,
            BuildIdentity.SourceRevision, options.CapabilityProfileJson, cancellationToken);
        // F15/V2: both local endpoints register through the ONE shared resolver, so this runtime and the
        // cloud Host registration share the exact retained volume identity instead of deriving a second
        // id for the same natural key.
        var leftVolumeId = await RegisterLocalVolumeAsync(authority, options.InstallationId, left, cancellationToken);
        var rightVolumeId = left.VolumeSerialNumber == right.VolumeSerialNumber ? leftVolumeId :
            await RegisterLocalVolumeAsync(authority, options.InstallationId, right, cancellationToken);
        var configuration = new SyncAuthorityPairConfiguration(runPair.PropagateDeletions ? "recoverable" : "disabled", 0, 1,
                "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                AutomaticPruning: false, RecoveryRetentionDays: options.RecoveryRetentionDays);
        var leftRootBindingId = Id("root-local", runPair.Id, left.IdentityToken);
        var rightRootBindingId = Id("root-right-local", runPair.Id, right.IdentityToken);
        var replacementRoots = replacedPair is null ? null : new SyncAuthorityDatabase.LocalPairReplacementRoots(
            options.InstallationId, replacedPair.Id, replacedPair.PolicyRevision, leftRootBindingId, leftVolumeId,
            rightRootBindingId, rightVolumeId);
        if (replacementRoots is not null)
            await authority.PrepareLocalPairReplacementAsync(runPair, configuration, replacementRoots, cancellationToken);
        else
        {
            await authority.RegisterPairAsync(runPair.Id, runPair.PolicyRevision, configuration, cancellationToken);
            await authority.RegisterLocalRootBindingAsync(leftRootBindingId, runPair.Id, 1, options.InstallationId,
                leftVolumeId, left.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                left.RootFileId.ToString("x16", CultureInfo.InvariantCulture), left.RootPath, cancellationToken,
                partitionCandidate: runPair);
            await authority.RegisterRootBindingAsync(rightRootBindingId, runPair.Id, "dropbox", 1,
                JsonSerializer.Serialize(new { localRootPath = right.RootPath, localRootIdentity = right.IdentityToken }),
                right.RootPath, cancellationToken, partitionCandidate: runPair);
        }
        using (var localRoot = PilotDirectoryLease.Open(left.RootPath, left.Identity))
        {
            localRoot.VerifyBinding();
            if (runPair.ControlNamespace is null)
            {
                var control = await new SyncAuthorityLocalControlNamespaceAllocator(authority).BindAsync(
                    localRoot, options.InstallationId, runPair.Id, leftRootBindingId, runPair.PolicyRevision,
                    options.ProfileId, cancellationToken, partitionCandidate: runPair);
                runPair = runPair with
                {
                    ControlNamespace = new SyncControlNamespaceBinding(options.InstallationId, runPair.Id,
                        control)
                };
            }
            using (SyncControlNamespace.OpenLocal(runPair.ControlNamespace!, localRoot))
            {
                // The bound private staging namespace is reverified before the run is admitted.
            }
        }
        SyncControlNamespaceBinding rightControl;
        using (var rightRoot = PilotDirectoryLease.Open(right.RootPath, right.Identity))
        {
            var namespaceId = RightControlNamespaceId(runPair.Id);
            if (runPair.RightControlNamespace is { } retainedControl)
            {
                if (retainedControl.InstallationId != options.InstallationId || retainedControl.NamespaceId != namespaceId)
                    throw new InvalidDataException("The right private namespace belongs to another installation or pair.");
                using var held = SyncControlNamespace.OpenLocal(retainedControl, rightRoot);
                rightControl = retainedControl;
            }
            else
            {
                var binding = await new SyncAuthorityLocalControlNamespaceAllocator(authority).BindAsync(
                    rightRoot, options.InstallationId, runPair.Id, rightRootBindingId, runPair.PolicyRevision,
                    options.ProfileId, cancellationToken, namespaceId: namespaceId, side: "dropbox",
                    partitionCandidate: runPair);
                rightControl = new SyncControlNamespaceBinding(options.InstallationId, namespaceId, binding);
            }
        }
        var store = CreateRightStore(resolution, rightControl);
        runPair = runPair with { RecoveryRoot = store.RecoveryBinding, RightControlNamespace = rightControl };
        var recoveryAuthorityId = Id("recovery-local", runPair.Id, right.IdentityToken);
        var plannerOptions = new SyncAuthorityPlannerOptions(runPair.PolicyRevision, options.ProfileId,
            runPair.ControlNamespace!.NamespaceId, recoveryAuthorityId,
            EnableCreateEffects: true,
            EnableReplacementEffects: true,
            EnableDeletionEffects: options.EnableDeletionEffects,
            EnableMoveEffects: options.EnableMoveEffects,
            DeletionMinimumCompleteObservations: 1,
            DeletionMinimumInterval: TimeSpan.Zero,
            Direction: runPair.Direction,
            RemoteUploadSessionPolicy: SyncUploadLimits.ForStore(store));
        return new SyncPreparedLocalPair(runPair, store, leftRootBindingId, rightRootBindingId, plannerOptions)
            { ReplacementRoots = replacementRoots };
    }

    /// <summary>Executes the prepared real endpoints through the existing authority runner.</summary>
    public static async Task<SyncRunResult> RunLocalToLocalAsync(SyncPair pair, SyncEndpointDocument document,
        SyncAuthorityDatabase authority, SyncLocalPairRuntimeOptions options, SyncAuthorityCommandAdmission admission,
        IReadOnlyList<SyncPairEndpoints>? retainedPairs = null, PilotRunControl? runControl = null,
        string trigger = "Run", Func<SyncComparisonProgress, Task>? onInventory = null,
        CancellationToken cancellationToken = default)
    {
        var activeControl = runControl ?? new PilotRunControl();
        var prepared = await PrepareLocalToLocalAsync(pair, document, authority, options, retainedPairs,
            activeControl, cancellationToken);
        var runPair = prepared.Pair;
        var runner = new SyncAuthorityPairRunner(runPair, prepared.Remote, authority, prepared.LeftRootBindingId,
            prepared.RightRootBindingId, prepared.PlannerOptions, activeControl, admission, cloudFiles: null, onInventory: onInventory);
        var result = new SyncRunResult(runPair.Id, Array.Empty<SyncDecision>());
        var decisions = new List<SyncDecision>();
        for (var pass = 0; pass < Math.Max(1, options.MaximumPlanningPasses); pass++)
        {
            result = await runner.RunAsync(trigger, cancellationToken);
            decisions.AddRange(result.Decisions);
            if (result.Decisions.All(decision => decision.Kind is SyncDecisionKind.Blocked or
                    SyncDecisionKind.Conflict or SyncDecisionKind.Stopped or SyncDecisionKind.ResponseUnknown))
                break;
            if (result.Decisions.All(decision => decision.Kind is SyncDecisionKind.Identical or
                    SyncDecisionKind.Baseline or SyncDecisionKind.Skipped))
                break;
            if (result.Decisions.Count == 0) break;
            if (!result.Decisions.Any(decision => decision.Kind is SyncDecisionKind.Uploaded or
                    SyncDecisionKind.Downloaded or SyncDecisionKind.Recovered or SyncDecisionKind.Archived))
                break;
            trigger = "Converge";
        }
        return new SyncRunResult(runPair.Id, decisions);
    }

    private static async Task<string> RegisterLocalVolumeAsync(SyncAuthorityDatabase authority, string installationId,
        SyncLocalEndpointBinding binding, CancellationToken cancellationToken)
    {
        var serialHex = binding.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
        return await authority.ResolveVolumeIdAsync(installationId, serialHex, "NTFS", null,
            supportsFileId128: false,
            supportsUsn: NtfsUsnJournal.TryReadCheckpoint(binding.RootPath, out _),
            supportsAtomicRename: true, cancellationToken);
    }

    private static SyncLocalEndpointBinding LocalBinding(SyncEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsLocal || endpoint.LocalIdentity is not { } identity)
            throw new SyncEndpointRuntimeRefusedException(Conclude(new List<SyncEndpointRefusal>
            {
                new(SyncEndpointError.MissingLocalIdentity,
                    "A local endpoint needs its stable volume serial and root file id before the runtime can bind it.")
            }));
        return new SyncLocalEndpointBinding(PilotPath.NormalizeLocalRoot(endpoint.LocalPath!),
            identity.VolumeSerialNumber, identity.RootFileId);
    }

    private static SyncEndpointValidation Conclude(List<SyncEndpointRefusal> refusals) =>
        refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);

    private static string Id(string role, params object[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" +
            string.Join("\n", parts.Select(part => Convert.ToString(part, CultureInfo.InvariantCulture))))))
        .ToLowerInvariant()[..32];
}
