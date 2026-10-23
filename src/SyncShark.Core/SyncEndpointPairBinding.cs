using System.Runtime.Versioning;

namespace Massdex.SyncShark;

/// <summary>
/// Converts the editor/CLI's ordered endpoints into the existing engine projection in one place.
/// Policy is supplied as an optional retained template; authority, approval and private directories
/// are never inherited here. The host verifies and retains those separately.
/// </summary>
public static class SyncEndpointPairDraft
{
    public static SyncPair Create(SyncPairEndpoints endpoints, string name, SyncPair? policy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!endpoints.Left.IsLocal && !endpoints.Right.IsLocal)
            return SyncCloudPairConfiguration.CreateDraft(endpoints, name, policy);
        var document = SyncEndpointPersistence.Symmetric(endpoints);
        var validation = SyncEndpointValidation.Combine(
            SyncEndpointContracts.ValidateRecord(document.Left!),
            SyncEndpointContracts.ValidateRecord(document.Right!),
            SyncEndpointContracts.ValidatePair(endpoints.Left, endpoints.Right));
        if (!validation.IsValid) throw new SyncEndpointRuntimeRefusedException(validation);
        if (!Enum.IsDefined(endpoints.Direction)) throw new InvalidDataException("Unknown pair direction.");
        if (SyncEndpointPairRuntime.BindingSupportReason(endpoints.Left.Kind, endpoints.Right.Kind) is { } reason)
            throw new NotSupportedException(reason);
        var layout = SyncEndpointEngineLayout.Create(endpoints);
        var left = layout.Local;
        var right = layout.Remote;
        var settings = new PilotSettings(left.LocalPath!, left.LocalIdentity!.VolumeSerialNumber,
            left.LocalIdentity.RootFileId, right.IsLocal ? right.LocalPath! : right.FolderPath!,
            right.IsLocal ? new SyncLocalEndpointBinding(right.LocalPath!, right.LocalIdentity!.VolumeSerialNumber,
                right.LocalIdentity.RootFileId).IdentityToken : right.FolderId!);
        var provider = right.IsLocal ? SyncProviders.Local : SyncEndpoints.ProviderLiteral(right.Kind)!;
        var id = SyncEndpointEngineLayout.DerivePairId(settings,
            right.IsLocal ? SyncProviders.Local : right.NamespaceId, layout.Reversed);
        return (policy ?? SyncPair.FromPilot(settings, name)) with
        {
            Id = id, Name = name, LocalRoot = settings.LocalRoot,
            LocalVolumeSerialNumber = settings.LocalVolumeSerialNumber, LocalRootFileId = settings.LocalRootFileId,
            DropboxRoot = settings.DropboxRoot, DropboxFolderId = settings.DropboxFolderId,
            Provider = provider, ConnectionId = right.ConnectionId ?? string.Empty,
            Direction = endpoints.Direction, EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left, RightEndpoint = document.Right,
            InitialReviewApproved = false, ControlNamespace = null, RightControlNamespace = null,
            RecoveryRoot = null, PolicyRevision = 1
        };
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>Shared endpoint-authoring boundary for the UI and CLI, including a running app.</summary>
    [SupportedOSPlatform("windows")]
    public async Task<SyncPairBindingResult> BindEndpointPairAsync(SyncPair draft,
        string? replacingPairId = null, long? expectedPolicyRevision = null,
        CancellationToken cancellationToken = default, DropboxFolderEntry? recoveryFolder = null,
        Action<string>? progress = null)
    {
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        return await BindEndpointPairCoreAsync(draft, replacingPairId, expectedPolicyRevision, cancellationToken,
            recoveryFolder, progress);
    }

    /// <summary>
    /// the normal endpoint-authoring core WITHOUT the configuration-mutation gate, so the composed
    /// shared recovery entry can hold the SAME existing gate across its whole validation/retention/publication
    /// flow without re-entering it. The public BindEndpointPairAsync acquires the gate exactly once and delegates
    /// here; every normal caller path is unchanged.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<SyncPairBindingResult> BindEndpointPairCoreAsync(SyncPair draft,
        string? replacingPairId = null, long? expectedPolicyRevision = null,
        CancellationToken cancellationToken = default, DropboxFolderEntry? recoveryFolder = null,
        Action<string>? progress = null, long? recoveredExpectedUnpublishedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        stateAuthority.VerifyAuthority();
        progress?.Invoke("Pair save: verifying retained database and installation state for the two selected endpoints.");
        await EnsureAuthorityInitializedAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: checking current file effects and configuration leases for the selected pair.");
        await EnsurePairConfigurationMutableAsync(replacingPairId, cancellationToken);
        draft = SyncPairStore.NormalizePair(draft);
        if (SyncCloudPairConfiguration.IsCloudOnly(draft))
            return await BindCloudEndpointPairAsync(draft, replacingPairId, expectedPolicyRevision, cancellationToken, progress);
        var document = draft.EndpointDocument ?? throw new InvalidDataException("Both explicit endpoints are required.");
        var resolution = SyncEndpointPairRuntime.Resolve(draft, document);
        if (!resolution.IsResolved) throw new SyncEndpointRuntimeRefusedException(resolution.Validation);
        if (resolution.IsLocalToLocal)
            return await BindLocalEndpointPairCoreAsync(draft, replacingPairId, expectedPolicyRevision, cancellationToken, progress);

        var right = SyncEndpointEngineLayout.Create(resolution.Endpoints!).Remote;
        var connection = LoadProviderConnections().SingleOrDefault(account => account.ConnectionId == right.ConnectionId);
        if (connection is null || !string.Equals(connection.Provider, draft.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connection.RootNamespaceId, right.NamespaceId, StringComparison.Ordinal))
            throw new InvalidDataException("The selected endpoint account or storage namespace changed. Choose its folder again.");
        // Do not create even the owned Recovery folder for a stale local selection.
        using var local = PilotDirectoryLease.Open(draft.LocalRoot);
        if (local.Identity.VolumeSerialNumber != draft.LocalVolumeSerialNumber || local.Identity.FileId != draft.LocalRootFileId)
            throw new IOException("The selected local folder identity changed. Choose the folder again.");
        var retained = await LoadPairsAsync(cancellationToken);
        var partitions = await RequireVerifiedPairPartitionsAsync(draft, replacingPairId, cancellationToken, progress);
        ValidateLocalEndpointOverlap(draft, retained.Pairs.Where(pair => pair.Id != replacingPairId), partitions);
        if (replacingPairId is not null && !retained.Pairs.Any(pair => pair.Id == replacingPairId &&
            (expectedPolicyRevision is null || pair.PolicyRevision == expectedPolicyRevision)))
            throw new InvalidOperationException("The pair changed after it was selected. Reload it before saving.");
        if (retained.Pairs.Any(pair => pair.Id != replacingPairId &&
            string.Equals(pair.Name, draft.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another folder pair already uses that name.");
        // An explicitly selected Recovery container is retained only when it is the one allowed
        // container for this provider; anything else falls back to preparing the recommended Recovery
        // exactly as before. The host owns the final account, root and Recovery identity check.
        DropboxFolderEntry recovery;
        if (recoveryFolder is { } selectedRecovery)
        {
            SyncDropboxArtifactPaths.RequireRecoveryContainer(selectedRecovery.Path);
            recovery = selectedRecovery;
        }
        else
        {
            progress?.Invoke("Pair save: locating or preparing the provider's owned Recovery container for these endpoints.");
            recovery = await EnsureRecommendedRecoveryFolderAsync(cancellationToken, draft.ConnectionId);
        }
        return await BindPairCoreAsync(new SyncPairBindingRequest(replacingPairId, draft.Name, draft.LocalRoot,
            new DropboxFolderEntry(draft.Name, draft.DropboxRoot, draft.DropboxFolderId), recovery,
            draft.Enabled, draft.Priority, draft.PropagateDeletions, draft.FilesOnDemandEnabled,
            draft.SelectionRules, draft.SelectionDefaultIncluded, draft.RecoveryRetentionDays, draft.Direction,
            draft.DirectoryStorageRules, draft.ConnectionId, draft.DefaultStorage, draft.IgnoreRules,
            expectedPolicyRevision, document), cancellationToken, progress,
            recoveredExpectedUnpublishedRevision);
    }

    private static SyncPair RetainVerifiedEndpoints(SyncPair verifiedPair, SyncEndpointDocument? requested,
        string namespaceId, string? endpointNamespaceId = null)
    {
        if (requested is null) return verifiedPair;
        var mapped = SyncEndpointsCompatibility.FromSyncPair(verifiedPair);
        var reversed = requested.Left is { IsLocal: false } && requested.Right is { IsLocal: true };
        var verified = mapped.Endpoints is null ? null : SyncEndpointPersistence.Symmetric(mapped.Endpoints);
        if (verified?.Right is { } right) verified = verified with
            { Right = right with { NamespaceId = endpointNamespaceId ?? namespaceId } };
        if (reversed && verified is not null) verified = verified with { Left = verified.Right, Right = verified.Left };
        if (!mapped.Validation.IsValid || verified != requested)
            throw new InvalidDataException("The verified folder identities differ from the chosen endpoints. Choose both folders again.");
        return verifiedPair with { Id = SyncEndpointEngineLayout.DerivePairId(verifiedPair.ToPilotSettings(), namespaceId, reversed),
            EndpointSchemaVersion = requested.Version,
            LeftEndpoint = requested.Left, RightEndpoint = requested.Right };
    }
}
