using System.Runtime.Versioning;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private static bool IsLocalPair(SyncPair pair) => SyncProviders.Normalize(pair.Provider) == SyncProviders.Local;

    /// <summary>
    /// Retains an explicit local/local draft after verifying both real root identities. Account-based
    /// pairs keep their existing binding entry point; no account or cloud root is fabricated here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<SyncPairBindingResult> BindLocalEndpointPairAsync(SyncPair draft,
        string? replacingPairId = null, long? expectedPolicyRevision = null,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await BindLocalEndpointPairCoreAsync(draft, replacingPairId,
            expectedPolicyRevision, cancellationToken, progress);
    }

    [SupportedOSPlatform("windows")]
    private async Task<SyncPairBindingResult> BindLocalEndpointPairCoreAsync(SyncPair draft,
        string? replacingPairId, long? expectedPolicyRevision,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        ArgumentNullException.ThrowIfNull(draft);
        stateAuthority.VerifyAuthority();
        await EnsureAuthorityInitializedAsync(cancellationToken, progress);
        await EnsurePairConfigurationMutableAsync(replacingPairId, cancellationToken);
        if (!IsLocalPair(draft)) throw new InvalidDataException("Select two local endpoints for this binding action.");
        draft = SyncPairStore.NormalizePair(draft);
        var retained = await LoadPairsAsync(cancellationToken);
        var prior = replacingPairId is null ? null : retained.Pairs.SingleOrDefault(pair => pair.Id == replacingPairId);
        if (replacingPairId is not null && prior is null)
            throw new InvalidDataException("The folder pair selected for replacement is no longer retained.");
        if (expectedPolicyRevision is { } revision && (revision < 1 || prior?.PolicyRevision != revision))
            throw new InvalidOperationException("The pair changed after it was selected. Reload it before saving.");
        if (retained.Pairs.Any(pair => pair.Id != replacingPairId &&
            (pair.Id == draft.Id || string.Equals(pair.Name, draft.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Another pair already uses this identity or name.");
        var expectedId = SyncPair.DeriveId(draft.ToPilotSettings(), SyncProviders.Local);
        var unchangedIdentity = prior is not null && IsLocalPair(prior) && SameRootIdentity(prior, draft);
        if (!unchangedIdentity && draft.Id != expectedId)
            throw new InvalidDataException("The pair identity must be derived from its two exact local roots.");
        if (prior?.FilesOnDemandEnabled == true)
            throw new InvalidOperationException("Disable Files On Demand on the existing pair before changing its endpoint type.");
        ValidateLocalEndpointOverlap(draft, retained.Pairs.Where(pair => pair.Id != replacingPairId));
        draft = draft with
        {
            Id = unchangedIdentity ? prior!.Id : draft.Id,
            PolicyRevision = unchangedIdentity ? (SamePolicy(prior!, draft) ? prior!.PolicyRevision : checked(prior!.PolicyRevision + 1)) : 1,
            InitialReviewApproved = unchangedIdentity ? prior!.InitialReviewApproved : false,
            ControlNamespace = unchangedIdentity ? prior!.ControlNamespace : null,
            RightControlNamespace = unchangedIdentity ? prior!.RightControlNamespace : null,
            RecoveryRoot = unchangedIdentity ? prior!.RecoveryRoot : null
        };
        var identityChanged = prior is not null && prior.Id != draft.Id;
        var endpoints = SyncEndpointPairRuntime.Resolve(draft, draft.EndpointDocument);
        using var leftLease = PilotDirectoryLease.Open(endpoints.LeftLocal!.RootPath, endpoints.LeftLocal.Identity);
        using var rightLease = PilotDirectoryLease.Open(endpoints.RightLocal!.RootPath, endpoints.RightLocal.Identity);
        progress?.Invoke("Pair save: verifying both held local folder identities and registering their owned staging and recovery bindings.");
        var prepared = await PrepareBoundLocalEndpointsAsync(draft, cancellationToken,
            replacingPair: identityChanged ? prior : null,
            retainedPairDefinitions: retained.Pairs.Where(item => item.Id != replacingPairId).ToArray());
        var pair = prepared.Pair;
        progress?.Invoke("Pair save: committing the verified local endpoint definition and policy in the retained database.");
        if (identityChanged)
        {
            leftLease.VerifyBinding();
            rightLease.VerifyBinding();
            await _authority.PublishLocalPairReplacementAsync(pair,
                prepared.ReplacementRoots ?? throw new InvalidOperationException("The replacement roots were not prepared."),
                cancellationToken);
        }
        else await _authority.RetainPairDefinitionAsync(pair, select: true, cancellationToken);
        _selectedPairId = pair.Id;
        var pairSet = await LoadPairsAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: committed local endpoint definition read back successfully; no sync was started by this save.");
        return new SyncPairBindingResult(pairSet, pair, pair.ToPilotSettings(), identityChanged);
    }

    private static void ValidateLocalEndpointOverlap(SyncPair candidate, IEnumerable<SyncPair> retained,
        IReadOnlyDictionary<string, SyncPairPartitionRelation>? verifiedPartitions = null)
    {
        var resolution = SyncEndpointPairRuntime.Resolve(candidate, candidate.EndpointDocument);
        if (!resolution.IsResolved) throw new SyncEndpointRuntimeRefusedException(resolution.Validation);
        foreach (var existing in retained)
        {
            var existingEndpoints = SyncCloudPairConfiguration.SavedEndpoints(existing)
                ?? throw new InvalidDataException("A retained pair has invalid endpoints. Repair it before binding an overlapping pair.");
            var roots = new[] { existingEndpoints.Left, existingEndpoints.Right }.Where(endpoint => endpoint.IsLocal);
            foreach (var oldRoot in roots)
            foreach (var proposedRoot in new[] { resolution.Endpoints!.Left, resolution.Endpoints.Right }.Where(endpoint => endpoint.IsLocal))
            {
                var validation = SyncEndpoints.ValidatePair(proposedRoot, oldRoot);
                if (!validation.IsValid)
                {
                    // A nested local root is admitted only when the enclosing pair proves by its own
                    // persisted selection/ignore policy that the whole nested subtree stays excluded on
                    // every side where the two pairs are nested. Equal roots, unproven nesting and any
                    // unverified identity or reserved control/recovery ownership still fail closed.
                    if (SyncPairPartitionPolicy.Decide(candidate, existing) == SyncPairPartitionRelation.NestedAllowed ||
                        SyncPairPartitionPolicy.DecideCorrespondingIncluded(candidate, existing) ==
                            SyncPairPartitionRelation.CorrespondingIncluded ||
                        verifiedPartitions is not null && verifiedPartitions.TryGetValue(existing.Id, out var verified) &&
                        verified is SyncPairPartitionRelation.NestedAllowed or
                            SyncPairPartitionRelation.CorrespondingIncluded)
                        continue;
                    throw new SyncEndpointRuntimeRefusedException(validation);
                }
            }
        }
    }

    private async Task<SyncPreparedLocalPair> PrepareBoundLocalEndpointsAsync(SyncPair pair,
        CancellationToken cancellationToken, bool forceReviewedDeletionEffects = false, SyncPair? replacingPair = null,
        IReadOnlyList<SyncPair>? retainedPairDefinitions = null)
    {
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken);
        var profileId = Id("profile", BuildIdentity.SourceRevision, CapabilityJson);
        var document = pair.EndpointDocument ?? throw new InvalidDataException("The local endpoint identities are missing.");
        var options = new SyncLocalPairRuntimeOptions(installationId, profileId, CapabilityJson,
                pair.RecoveryRetentionDays ?? int.MaxValue,
                EnableDeletionEffects: pair.PropagateDeletions || forceReviewedDeletionEffects,
                EnableMoveEffects: true);
        return replacingPair is null
            ? await SyncEndpointPairRuntime.PrepareLocalToLocalAsync(pair, document, _authority, options,
                cancellationToken: cancellationToken, retainedPairDefinitions: retainedPairDefinitions)
            : await SyncEndpointPairRuntime.PrepareLocalToLocalReplacementAsync(pair, document, _authority,
                options, replacingPair, cancellationToken);
    }

    private async Task<PreparedPair> PrepareLocalPairAsync(SyncPair pair, CancellationToken cancellationToken,
        bool forceReviewedDeletionEffects)
    {
        if (!await _authority.IsPairDefinitionRetainedAsync(pair.Id, cancellationToken))
            throw new InvalidOperationException("Bind and verify both local roots before syncing this pair.");
        var retained = await _authority.LoadPairSetAsync(cancellationToken);
        ValidateLocalEndpointOverlap(pair, retained.Pairs.Where(other => other.Id != pair.Id));
        var prepared = await PrepareBoundLocalEndpointsAsync(pair, cancellationToken, forceReviewedDeletionEffects,
            retainedPairDefinitions: retained.Pairs.Where(other => other.Id != pair.Id).ToArray());
        // Runtime must not silently replace the persisted recovery/control binding. Binding is a
        // separate shared action; the same retained identities reach every runner consumer.
        if (pair.ControlNamespace != prepared.Pair.ControlNamespace || pair.RecoveryRoot != prepared.Pair.RecoveryRoot ||
            pair.RightControlNamespace != prepared.Pair.RightControlNamespace)
            throw new InvalidDataException("The retained local staging or recovery identity changed. Rebind the pair before running.");
        return new PreparedPair(prepared.Remote, prepared.LeftRootBindingId, prepared.RightRootBindingId,
            prepared.PlannerOptions, null);
    }
}
