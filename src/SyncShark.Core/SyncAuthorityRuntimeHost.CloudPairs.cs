using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// CC4A: the one internal Host cloud/cloud binding and read-only preparation path.
//
// This partial adds the real Host binding/preparation implementation for an explicit cloud/cloud pair. It
// is deliberately NOT activation: every public enablement gate stays exactly as it was, no cloud/cloud pair
// becomes runnable and no consumer is wired here. The runner packet consumes the prepared context this file
// produces.
//
// The path is provider-neutral apart from ONE seam - the central endpoint adapter factory below - which is
// the only place a registered provider selects an adapter. Everything else works through the CC1 typed
// endpoint projection, the CC2B account identity mapping, the CC2C typed ports and root registration, the
// CC2D verified hierarchy readback and the CC2E exact account+corpus recovery scope.
//
// The pair's private staging identity is NOT either endpoint and is not a pair member: it is the existing
// private control namespace allocated beside the product's real state anchor, reopened read-only for
// ordinary preparation and created once only by the separate binding action.

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>
    /// The bounded in-process phase seam the focused proofs use. It has no production default, performs no
    /// provider, filesystem or journal effect itself, and never changes a decision.
    /// </summary>
    internal static Action<string>? CloudPairBindingPhaseForTest { get; set; }

    // ---------------------------------------------------------------------------------------------------
    // CC4A-1: the one central endpoint adapter factory
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The exact retained account facts one endpoint is reached through.</summary>
    private sealed record CloudAccountFacts(string Provider, string ConnectionId, string ProviderAccountId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId);

    /// <summary>
    /// CC4A-1: the ONE provider-specific dispatch seam of this packet. It verifies the registered provider
    /// definition, the exact retained account identity tuple, the explicit native namespace and the stable
    /// selected folder identity, then constructs the existing adapter for that provider. An unknown or
    /// missing account/provider is an explicit refusal; a Dropbox adapter or a default account is never
    /// substituted for another provider or for an unnamed account.
    /// </summary>
    private ISyncRemoteStore CreateCloudEndpointAdapter(SyncEndpointRecord record, CloudAccountFacts facts)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(facts);
        if (!SyncEndpointProviders.TryGet(record.Provider, out var definition) || definition!.ProviderId is null)
            throw new NotSupportedException(
                "The declared endpoint provider has no registered cloud adapter in this build; the endpoint " +
                "is refused rather than served by a default provider.");
        if (definition.Kind != record.Kind)
            throw new InvalidDataException(
                "The registered provider does not match the endpoint's own kind; the endpoint is refused.");
        if (string.IsNullOrWhiteSpace(record.ConnectionId))
            throw new InvalidOperationException(
                "The endpoint names no exact account to reach it through; a default or currently selected " +
                "account is never substituted.");
        if (!string.Equals(record.ConnectionId, facts.ConnectionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The resolved account facts are not the account this endpoint names; the endpoint is refused.");
        // The retained provider-account id must be exactly this provider's derivation over that row's own
        // account/root/home tuple, and the endpoint namespace must be exactly the retained one.
        SyncAuthorityProviderAccountIdentity.RequireRetainedEndpointMatches(definition.ProviderId,
            facts.ProviderAccountId, facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId,
            record.NamespaceId);
        if (string.IsNullOrWhiteSpace(record.FolderId))
            throw new InvalidDataException(
                "The endpoint names no stable provider folder identity; a display path alone is never an " +
                "identity.");
        return definition.Kind switch
        {
            SyncEndpointKind.Dropbox => CreateDropboxCloudEndpointAdapter(record, facts),
            SyncEndpointKind.GoogleDrive => CreateGoogleCloudEndpointAdapter(record, facts),
            _ => throw new NotSupportedException(
                "The registered provider has no cloud endpoint adapter in this build; the endpoint is refused.")
        };
    }

    private ISyncRemoteStore CreateDropboxCloudEndpointAdapter(SyncEndpointRecord record, CloudAccountFacts facts)
    {
        var connection = connectionStore.Load(record.ConnectionId)
                         ?? throw new InvalidOperationException(
                             "Connect the exact verified Dropbox account this endpoint names; another account " +
                             "is never substituted.");
        if (!string.Equals(connection.ConnectionId, facts.ConnectionId, StringComparison.Ordinal) ||
            !string.Equals(connection.AccountId, facts.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained Dropbox connection is not the exact account this endpoint names; the endpoint is refused.");
        if (string.IsNullOrWhiteSpace(connection.RootNamespaceId) ||
            !string.Equals(connection.RootNamespaceId, facts.RootNamespaceId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained Dropbox account no longer carries this endpoint's verified root namespace.");
        return new DropboxPilotClient(httpClient, AccountTokenSource(connection),
            record.FolderPath ?? string.Empty, connection, record.FolderId!, _transferLimiter);
    }

    private ISyncRemoteStore CreateGoogleCloudEndpointAdapter(SyncEndpointRecord record, CloudAccountFacts facts)
    {
        var binding = _googleDriveConnections.Load(record.ConnectionId)
                      ?? throw new GoogleDriveBlockedException(
                          "This endpoint is bound to a Google Drive account that is not connected. Reconnect " +
                          "that exact account; another account is never substituted.");
        GoogleDriveProviderConfiguration.ValidateConnection(binding);
        if (!string.Equals(binding.ConnectionId, facts.ConnectionId, StringComparison.Ordinal) ||
            !string.Equals(binding.PermissionId, facts.AccountId, StringComparison.Ordinal) ||
            !string.Equals(binding.AccountKey, facts.RootNamespaceId, StringComparison.Ordinal))
            throw new GoogleDriveBlockedException(
                "The connected Google Drive account is not the exact account this endpoint names; the " +
                "endpoint is refused.");
        return new GoogleDrivePilotClient(httpClient, GoogleTokenSource(binding), binding, record.FolderId!,
            record.FolderPath ?? string.Empty, _transferLimiter,
            allowSharedDrives: binding.SharedDrivesEnabled);
    }

    // ---------------------------------------------------------------------------------------------------
    // Account resolution: the exact retained account of one explicit endpoint
    // ---------------------------------------------------------------------------------------------------

    private CloudAccountFacts ResolveCloudAccount(SyncEndpointKind kind, string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException(
                "Each cloud endpoint requires its exact retained account; a missing account is refused " +
                "rather than resolved from a default or currently selected account.");
        var definition = SyncEndpointProviders.Get(kind);
        if (definition.ProviderId is null)
            throw new NotSupportedException("The endpoint kind names no registered cloud provider.");
        if (kind == SyncEndpointKind.Dropbox)
        {
            var connection = connectionStore.Load(connectionId)
                             ?? throw new InvalidOperationException(
                                 "Connect the exact verified Dropbox account this endpoint names.");
            if (!string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained Dropbox connection is not the account this endpoint names.");
            var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(definition.ProviderId,
                connection.AccountId, connection.RootNamespaceId, connection.HomeNamespaceId);
            return new CloudAccountFacts(definition.ProviderId, connection.ConnectionId, providerAccountId,
                connection.AccountId, connection.RootNamespaceId, connection.HomeNamespaceId);
        }
        if (kind == SyncEndpointKind.GoogleDrive)
        {
            var binding = _googleDriveConnections.Load(connectionId)
                          ?? throw new GoogleDriveBlockedException(
                              "Reconnect this endpoint's exact Google Drive account.");
            GoogleDriveProviderConfiguration.ValidateConnection(binding);
            if (!string.Equals(binding.ConnectionId, connectionId, StringComparison.Ordinal))
                throw new GoogleDriveBlockedException(
                    "The retained Google Drive account is not the account this endpoint names.");
            // The retained Google row keeps the native permission id in its account column and the account
            // key in its root/home columns; the identity helper owns that mapping, not this caller.
            var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(definition.ProviderId,
                binding.PermissionId, binding.AccountKey, binding.AccountKey);
            return new CloudAccountFacts(definition.ProviderId, binding.ConnectionId, providerAccountId,
                binding.PermissionId, binding.AccountKey, binding.AccountKey);
        }
        throw new NotSupportedException(
            "This registered provider has no retained account mapping in this build; the endpoint is refused.");
    }

    // ---------------------------------------------------------------------------------------------------
    // Pair-level structural gate
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The CC1 structural projection is the only admission test for this path. A refused, incomplete or
    /// local-containing pair never reaches the cloud binding code, and ignoring the CC1 unverified-ancestry
    /// refinement is never treated here as an assertion that two roots are disjoint: the live comparison
    /// below decides that.
    /// </summary>
    private static SyncPairEndpoints RequireCloudOnlyProjection(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!SyncCloudPairConfiguration.IsCloudOnly(pair))
            throw new InvalidDataException(
                "This action binds an explicit cloud/cloud pair only; a local-containing or legacy pair is " +
                "never routed through the cloud path.");
        var validation = SyncCloudPairConfiguration.ValidateProjection(pair);
        if (!validation.IsValid) throw new SyncEndpointRuntimeRefusedException(validation);
        return SyncCloudPairConfiguration.RequireProjection(pair);
    }

    // -------------------------------------------------------------------------------------------------
    // CC4A: the real Host prepare/bind path for an explicit cloud/cloud pair
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The one Host preparation request for an explicit cloud/cloud pair. The caller names only the pair and
    /// the two retained authority root binding ids it is bound under: it never supplies a provider account, a
    /// namespace, a folder identity, a corpus or a trusted ancestry, because every one of those is resolved
    /// from the retained rows and read back from the provider through each root's own bound account.
    /// </summary>
    internal sealed record CloudPairHostPrepareRequest(
        SyncPair Pair,
        string LeftRootBindingId,
        string RightRootBindingId,
        string? ReplacingPairId = null,
        long? ExpectedPolicyRevision = null,
        long Generation = 1,
        bool CreatePrivateStaging = false,
        // H2: the edit/pair-retirement guard is part of CONFIGURATION editing, never of read-only runtime
        // preparation. It defaults to the guard so an unnamed caller keeps today's editing admission, and the
        // runtime preparation route opts out explicitly rather than by omission.
        bool RequireConfigurationMutable = true);

    /// <summary>One resolved cloud side: its exact endpoint record, retained account facts and adapter.</summary>
    private sealed record CloudSideResolution(SyncEndpointRecord Record, CloudAccountFacts Facts,
        ISyncRemoteStore Adapter);

    /// <summary>One prepared logical side: the exact retained authority and the chain it was verified with.</summary>
    private sealed record CloudPreparedSide(SyncAuthorityCloudSideAuthority Authority,
        SyncAuthorityCloudHierarchySnapshot RecoverySnapshot)
    {
        internal string RecoveryRootBindingId => Authority.RecoveryRootBindingId;
    }

    /// <summary>The shared simple-upload authority of both logical cloud targets; a limit, not a name.</summary>

    /// <summary>
    /// CC4AHOST-1: the Host entry admission and the one real prepare/bind path. It refuses unknown,
    /// unreachable, missing or stale evidence closed with zero partial authority activation: every decision is
    /// made before the corresponding write, and a refused step leaves no root, recovery or staging row behind
    /// that a later runner could consume.
    ///
    /// CC4AHOST-2: each side's account is resolved through the existing central factory from that endpoint's
    /// own retained connection, never from a caller claim.
    ///
    /// CC4AHOST-3: the candidate root is read back from its own provider, and every retained cloud authority
    /// of the same provider is read back through ITS own bound account from the current narrow snapshot, so
    /// the full retained set is rechecked and no retained root is omitted.
    ///
    /// CC4AHOST-4: a root is registered only through the existing transactional
    /// RegisterCloudEndpointRootBindingAsync, with no new registration path and no guard weakening.
    ///
    /// CC4AHOST-5: the exact CC2E account+corpus recovery scope is resolved with recommended-recovery-folder
    /// handling, and the read-only InspectBoundAsync reopen gains its real caller.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal async Task<SyncAuthorityPreparedCloudPair> PrepareCloudPairBindingAsync(
        CloudPairHostPrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        stateAuthority.VerifyAuthority();
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken);
        // H2 correction: an UNCHANGED cloud pair must be able to reach the code that resolves its own retained
        // pending operation. The pair-mutability guard belongs to a genuine configuration change, so it is
        // applied only when the exact caller is editing the pair (the default) and is omitted for read-only
        // runtime preparation, which changes no retained configuration. Every provider, root, account, policy
        // and identity check below is unchanged.
        if (request.RequireConfigurationMutable)
            await EnsurePairConfigurationMutableAsync(request.ReplacingPairId, cancellationToken);
        else
            await RequireUnchangedRuntimePairAsync(request, cancellationToken);
        var pair = SyncPairStore.NormalizePair(request.Pair);
        var endpoints = RequireCloudOnlyProjection(pair);
        if (pair.EndpointDocument is null)
            throw new InvalidDataException(
                "The pair carries no explicit cloud endpoint document, so no cloud root is prepared.");
        if (string.IsNullOrWhiteSpace(request.LeftRootBindingId) ||
            string.IsNullOrWhiteSpace(request.RightRootBindingId))
            throw new InvalidDataException(
                "Each logical side requires its exact retained authority root binding id; an unnamed root " +
                "is refused.");
        if (string.Equals(request.LeftRootBindingId, request.RightRootBindingId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The two logical sides require two distinct retained authority root binding ids.");
        if (request.Generation < 1) throw new ArgumentOutOfRangeException(nameof(request.Generation));
        var retainedPairs = await LoadPairsAsync(cancellationToken);
        var prior = request.ReplacingPairId is null
            ? null
            : retainedPairs.Pairs.SingleOrDefault(item => item.Id == request.ReplacingPairId);
        if (request.ReplacingPairId is not null && prior is null)
            throw new InvalidDataException("The cloud pair selected for replacement is no longer retained.");
        if (request.ExpectedPolicyRevision is { } revision && (revision < 1 || prior?.PolicyRevision != revision))
            throw new InvalidOperationException("The pair changed after it was selected. Reload it before saving.");
        if (retainedPairs.Pairs.Any(item => item.Id != request.ReplacingPairId &&
                (item.Id == pair.Id || string.Equals(item.Name, pair.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Another pair already uses this identity or name.");
        if (prior is not null && prior.Id != pair.Id)
            throw new InvalidDataException(
                "The pair identity must be derived from its two exact cloud endpoints; a replacement never " +
                "silently renames an existing pair here.");

        var leftRecord = SyncEndpointRecord.From(endpoints.Left);
        var rightRecord = SyncEndpointRecord.From(endpoints.Right);
        if (leftRecord.IsLocal || rightRecord.IsLocal)
            throw new InvalidDataException(
                "This path requires two cloud endpoints; a local folder is never a cloud root and a fake " +
                "local endpoint is not a substitute.");
        if (string.IsNullOrWhiteSpace(leftRecord.Provider) || string.IsNullOrWhiteSpace(rightRecord.Provider))
            throw new InvalidDataException(
                "Each cloud endpoint requires its registered provider literal; an unnamed provider is refused.");

        // CC4AHOST-2: per-side account resolution through the ONE existing factory seam.
        var left = ResolveCloudSide(leftRecord);
        var right = ResolveCloudSide(rightRecord);

        // CC4AHOST-3/4: read the candidate from its own provider, then register it through the existing
        // transaction with the complete retained set rechecked.
        // read BOTH fresh provider-issued candidate hierarchies BEFORE registering either root, and
        // register the pair's two cloud roots through ONE narrow guarded ATOMIC two-sided operation, so the
        // unchanged proof resolver can see both genuine DB-held identities for an opaque corresponding pair.
        var leftCandidate = await ReadCandidateHierarchyAsync(left, request.LeftRootBindingId, cancellationToken);
        var rightCandidate = await ReadCandidateHierarchyAsync(right, request.RightRootBindingId, cancellationToken);
        var evidence = new List<SyncAuthorityCloudHierarchySnapshot> { leftCandidate, rightCandidate };
        foreach (var authority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            // The candidate pair's OWN roots are already in the evidence list; a retained authority that is
            // one of them would bind the same identity twice and make the guard refuse ambiguous evidence.
            if (string.Equals(authority.AuthorityId, request.LeftRootBindingId, StringComparison.Ordinal) ||
                string.Equals(authority.AuthorityId, request.RightRootBindingId, StringComparison.Ordinal)) continue;
            if (!string.Equals(authority.Provider, left.Facts.Provider, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authority.Provider, right.Facts.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            evidence.Add(await ReadRetainedAuthorityHierarchyAsync(authority, cancellationToken));
        }
        await _authority.RegisterTwoSidedCandidateCloudRootsAsync(pair.Id, pair.PolicyRevision, installationId,
            request.Generation,
            new SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot(request.LeftRootBindingId,
                SyncAuthorityCloudSides.LogicalLeft, left.Facts.Provider, left.Facts.ProviderAccountId,
                left.Record.NamespaceId!, left.Record.FolderId!,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(left.Record.FolderPath)),
            new SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot(request.RightRootBindingId,
                SyncAuthorityCloudSides.LogicalRight, right.Facts.Provider, right.Facts.ProviderAccountId,
                right.Record.NamespaceId!, right.Record.FolderId!,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(right.Record.FolderPath)),
            pair, evidence, cancellationToken);
        // D2: retain the two verified observations only AFTER the successful root transaction.
        await _authority.RetainVerifiedPairRootObservationAsync(pair.Id, SyncAuthorityCloudSides.LogicalLeft,
            leftCandidate, cancellationToken);
        await _authority.RetainVerifiedPairRootObservationAsync(pair.Id, SyncAuthorityCloudSides.LogicalRight,
            rightCandidate, cancellationToken);

        // CC4AHOST-5: the exact CC2E recovery scope, the recommended Recovery location and the read-only
        // private staging reopen.
        var leftSide = await ResolveCloudSideRecoveryAsync(left, pair.Id, SyncAuthorityCloudSides.LogicalLeft,
            request.LeftRootBindingId, leftCandidate, installationId, cancellationToken);
        var rightSide = await ResolveCloudSideRecoveryAsync(right, pair.Id, SyncAuthorityCloudSides.LogicalRight,
            request.RightRootBindingId, rightCandidate, installationId, cancellationToken);
        var staging = await ReopenPrivateStagingAsync(pair.Id, request.LeftRootBindingId, pair.PolicyRevision,
            installationId, request.CreatePrivateStaging, cancellationToken);
        var portSet = SyncAuthorityCloudEndpointSet.FromPair(pair, left.Adapter, right.Adapter,
            request.LeftRootBindingId, request.RightRootBindingId, left.Facts.ProviderAccountId,
            right.Facts.ProviderAccountId);
        var profileId = Id("profile", BuildIdentity.SourceRevision, CapabilityJson);
        // CC4B replacement admission, approved with its exact safety meaning: a SUCCESSFULLY prepared cloud/cloud
        // context - both exact side recovery authorities already resolved and this pair's private staging already
        // reopened above - permits PLANNING a replacement, so the generic update slice can be planned. This is
        // planning admission only: it is not a global cloud flag, not a user setting, not a caller-supplied
        // recoveryReady bypass, and it relaxes no preparation. Every executor-side authority requirement, the
        // predecessor and source checks, the conditional revision, pause, direction and policy admission and all
        // uncertainty and replay protection stay exactly as they are, so recovery drifting after preparation
        // still refuses the replacement. Deletion and move effects stay false and the unsupported conflict,
        // delete and move dispatch restrictions are unchanged.
        var plannerOptions = new SyncAuthorityPlannerOptions(pair.PolicyRevision, profileId, staging.NamespaceId,
            leftSide.RecoveryRootBindingId, EnableCreateEffects: true, EnableReplacementEffects: true,
            EnableDeletionEffects: false, EnableMoveEffects: false, DeletionMinimumCompleteObservations: 1,
            DeletionMinimumInterval: TimeSpan.Zero, Direction: pair.Direction);
        var leftUploadPolicy = SyncUploadLimits.ForStore(left.Adapter);
        var rightUploadPolicy = SyncUploadLimits.ForStore(right.Adapter);
        var allocations = new SyncAuthorityCloudAllocationConfiguration(staging.NamespaceId,
            new SyncAuthorityCloudTargetAllocation(leftSide.RecoveryRootBindingId,
                leftUploadPolicy.MaximumSimpleCreateBytes, leftUploadPolicy.MaximumSimpleReplacementBytes),
            new SyncAuthorityCloudTargetAllocation(rightSide.RecoveryRootBindingId,
                rightUploadPolicy.MaximumSimpleCreateBytes, rightUploadPolicy.MaximumSimpleReplacementBytes));
        allocations.Validate();
        return new SyncAuthorityPreparedCloudPair(pair, portSet, staging, leftSide.Authority, rightSide.Authority,
            plannerOptions, allocations,
            new[] { leftCandidate, rightCandidate, leftSide.RecoverySnapshot, rightSide.RecoverySnapshot });
    }

    // CC4AHOST-2: resolve the endpoint's own retained account through the existing factory, then construct
    // the existing registered-provider adapter for exactly that account.
    private CloudSideResolution ResolveCloudSide(SyncEndpointRecord record)
    {
        var facts = ResolveCloudAccount(record.Kind, record.ConnectionId);
        return new CloudSideResolution(record, facts, CreateCloudEndpointAdapter(record, facts));
    }

    // CC4AHOST-3: one root's own provider-issued chain, read through that root's own adapter.
    private static async Task<SyncAuthorityCloudHierarchySnapshot> ReadCandidateHierarchyAsync(
        CloudSideResolution side, string rootBindingId, CancellationToken cancellationToken)
    {
        var evidence = await SyncAuthorityCloudRootHierarchy.ReadAsync(side.Adapter,
            HierarchyRequestFor(side.Record, side.Facts), cancellationToken);
        return evidence.ToSnapshot(rootBindingId);
    }

    private static SyncAuthorityCloudRootHierarchyRequest HierarchyRequestFor(SyncEndpointRecord record,
        CloudAccountFacts facts) =>
        new(facts.Provider, facts.ConnectionId, facts.ProviderAccountId, record.NamespaceId!,
            record.FolderId!, SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(record.FolderPath));

    /// <summary>
    /// CC4AHOST-3/4: register one candidate root through the existing transactional registration.
    /// Immediately before the call the CURRENT retained set is read through the narrow snapshot and every
    /// retained authority of the candidate's own provider is re-read from the provider through its OWN bound
    /// account, so the transaction rechecks the complete retained set and no retained root is omitted or
    /// accepted unverified.
    /// </summary>
    private async Task RegisterCandidateCloudRootAsync(string pairId, string side, CloudSideResolution candidate,
        string rootBindingId, long generation, string installationId,
        SyncAuthorityCloudHierarchySnapshot candidateSnapshot, CancellationToken cancellationToken,
        SyncPair? partitionCandidate = null)
    {
        var chains = new List<SyncAuthorityCloudHierarchySnapshot> { candidateSnapshot };
        foreach (var authority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            if (string.Equals(authority.AuthorityId, rootBindingId, StringComparison.Ordinal)) continue;
            if (!string.Equals(authority.Provider, candidate.Facts.Provider, StringComparison.OrdinalIgnoreCase))
                continue;
            chains.Add(await ReadRetainedAuthorityHierarchyAsync(authority, cancellationToken));
        }
        if (partitionCandidate is null)
            await _authority.RegisterCloudEndpointRootBindingAsync(rootBindingId, pairId, side, generation,
                installationId, candidate.Facts.Provider, candidate.Facts.ProviderAccountId,
                candidate.Record.NamespaceId!, candidate.Record.FolderId!,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(candidate.Record.FolderPath),
                cancellationToken, chains);
        else
            await _authority.RegisterCloudEndpointRootBindingForCandidateAsync(rootBindingId, pairId, side,
                generation, installationId, candidate.Facts.Provider, candidate.Facts.ProviderAccountId,
                candidate.Record.NamespaceId!, candidate.Record.FolderId!,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(candidate.Record.FolderPath),
                partitionCandidate, cancellationToken, chains);
    }

    /// <summary>
    /// One retained authority's fresh provider-issued chain, read through that retained root's own bound
    /// account. An authority whose exact account is no longer connected, or whose adapter refuses the read,
    /// fails the caller closed rather than being dropped from the comparison set.
    /// </summary>
    private async Task<SyncAuthorityCloudHierarchySnapshot> ReadRetainedAuthorityHierarchyAsync(
        SyncAuthorityCloudRootIdentity authority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authority.ProviderAccountId) ||
            string.IsNullOrWhiteSpace(authority.NamespaceId) || string.IsNullOrWhiteSpace(authority.Provider))
            throw new InvalidOperationException(
                "The retained cloud authority " + authority.AuthorityId + " carries no exact provider " +
                "account and namespace, so its ancestry can never be verified; the registration is refused " +
                "rather than treating an unverifiable retained root as disjoint.");
        if (!SyncEndpointProviders.TryGet(authority.Provider, out var retainedDefinition) ||
            retainedDefinition!.ProviderId is null)
            throw new NotSupportedException(
                "The retained cloud authority names no registered cloud provider; it is refused.");
        var record = new SyncEndpointRecord(retainedDefinition.Kind,
            Provider: authority.Provider, ConnectionId: RetainedAuthorityConnectionId(authority),
            NamespaceId: authority.NamespaceId, FolderId: authority.FolderId, FolderPath: authority.FolderPath);
        var side = ResolveCloudSide(record);
        if (!string.Equals(side.Facts.ProviderAccountId, authority.ProviderAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained cloud authority " + authority.AuthorityId + " no longer resolves to its own " +
                "retained provider account; the registration is refused.");
        return await ReadCandidateHierarchyAsync(side, authority.AuthorityId, cancellationToken);
    }

    /// <summary>The exact retained connection of one retained authority's own provider account.</summary>
    private string RetainedAuthorityConnectionId(SyncAuthorityCloudRootIdentity authority)
    {
        var provider = authority.Provider!;
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new NotSupportedException(
                "The retained cloud authority names no registered cloud provider; it is refused.");
        if (definition.Kind == SyncEndpointKind.Dropbox)
        {
            foreach (var connection in connectionStore.LoadActiveBindings())
                if (string.Equals(SyncAuthorityProviderAccountIdentity.DeriveId(provider, connection.AccountId,
                        connection.RootNamespaceId, connection.HomeNamespaceId), authority.ProviderAccountId,
                        StringComparison.Ordinal))
                    return connection.ConnectionId;
        }
        else if (definition.Kind == SyncEndpointKind.GoogleDrive)
        {
            foreach (var binding in _googleDriveConnections.LoadActiveBindings())
                if (string.Equals(SyncAuthorityProviderAccountIdentity.DeriveId(provider, binding.PermissionId,
                        binding.AccountKey, binding.AccountKey), authority.ProviderAccountId,
                        StringComparison.Ordinal))
                    return binding.ConnectionId;
        }
        throw new InvalidOperationException(
            "The retained cloud authority " + authority.AuthorityId + " names no connected account of its " +
            "own provider, so its ancestry cannot be verified; the registration is refused rather than " +
            "omitting a retained root from the comparison set.");
    }

    /// <summary>
    /// CC4AHOST-5: resolve one logical side's exact CC2E recovery authority per provider account AND actual
    /// corpus. The recommended Recovery location is ensured through the existing folder-browser path, a
    /// selected sync root that contains that location is refused, and the retained recovery root is created
    /// or reused only through the existing scoped registration.
    /// </summary>
    private async Task<CloudPreparedSide> ResolveCloudSideRecoveryAsync(CloudSideResolution side, string pairId,
        string logicalSide, string rootBindingId, SyncAuthorityCloudHierarchySnapshot candidateSnapshot,
        string installationId, CancellationToken cancellationToken)
    {
        var corpus = candidateSnapshot.CorpusRootId ?? throw new InvalidOperationException(
            "The candidate root has no positively identified corpus root, so no recovery authority can be " +
            "scoped to it; the preparation is refused.");
        var recoveryPath = SyncDropboxArtifactPaths.RecoveryContainerPath;
        if (!string.IsNullOrWhiteSpace(side.Record.FolderPath) &&
            !RecoveryPlacementAllowed(side.Record.FolderPath!, recoveryPath))
            throw new InvalidOperationException(
                "The selected cloud root already contains the recommended Recovery location '" + recoveryPath +
                "'. A dedicated recovery choice is required; a recovery copy is never created inside a " +
                "selected sync scope.");
        var browser = await CreateFolderBrowserAsync(cancellationToken, side.Facts.ConnectionId);
        var recommended = await EnsureRecommendedRecoveryFolderCoreAsync(browser, Array.Empty<SyncPair>(),
            cancellationToken);
        var recoveryRecord = side.Record with { FolderId = recommended.FolderId, FolderPath = recommended.Path };
        var recoverySide = ResolveCloudSide(recoveryRecord);
        var recoveryRootBindingId = Id("recovery-cloud", pairId, logicalSide);
        var recoverySnapshot = await ReadCandidateHierarchyAsync(recoverySide, recoveryRootBindingId,
            cancellationToken);
        var retained = await _authority.FindCloudRecoveryRootAsync(installationId, side.Facts.Provider,
            side.Facts.ProviderAccountId, recoveryRecord.NamespaceId!, recommended.FolderId, corpus,
            cancellationToken);
        if (retained is null)
        {
            var chains = new List<SyncAuthorityCloudHierarchySnapshot> { recoverySnapshot };
            foreach (var retainedAuthority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
            {
                if (!string.Equals(retainedAuthority.Provider, side.Facts.Provider, StringComparison.OrdinalIgnoreCase))
                    continue;
                chains.Add(await ReadRetainedAuthorityHierarchyAsync(retainedAuthority, cancellationToken));
            }
            retained = await _authority.RegisterCloudRecoveryRootAsync(recoveryRootBindingId, installationId,
                side.Facts.Provider, side.Facts.ProviderAccountId, recoveryRecord.NamespaceId!,
                recommended.FolderId, corpus, recommended.Path, 1, cancellationToken, chains);
    }
        var authority = new SyncAuthorityCloudSideAuthority(logicalSide, side.Facts.Provider,
            side.Facts.ConnectionId, side.Facts.ProviderAccountId, side.Record.NamespaceId!,
            side.Record.FolderId!, side.Record.FolderPath ?? string.Empty, rootBindingId,
            retained.RecoveryRootBindingId, retained.FolderId, retained.NamespaceId!,
            retained.ProviderCorpusId!, retained.DisplayPath);
        return new CloudPreparedSide(authority, recoverySnapshot);
    }

    /// <summary>
    /// CC4AHOST-5: the read-only reopen of this pair's completed private staging identity beside the real
    /// product state anchor. Ordinary preparation never creates, adopts or reallocates one; only the separate
    /// binding action creates it once through the existing allocator.
    ///
    /// CC4A-3: the private staging allocation is a REAL local allocation beside the state anchor, so its
    /// operation member must be attributed to this pair's own retained logical-left authority root - not to
    /// the pair id, not to either logical side's remote recovery authority and not to a synthesized
    /// placeholder. The exact reference the database actually retains as this pair's active logical-left root
    /// is therefore resolved here and required to be the very left root this preparation verified, before any
    /// physical staging allocation exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<SyncControlNamespaceBinding> ReopenPrivateStagingAsync(string pairId,
        string leftRootBindingId, long policyRevision, string installationId, bool createOnce,
        CancellationToken cancellationToken)
    {
        using var anchor = PilotDirectoryLease.Open(stateAuthority.StateRoot);
        anchor.VerifyBinding();
        var allocator = new SyncAuthorityLocalControlNamespaceAllocator(_authority);
        if (!createOnce)
            return new SyncControlNamespaceBinding(installationId, pairId,
                await allocator.InspectBoundAsync(anchor, pairId, cancellationToken));
        var retainedLeftRoot = await _authority.LoadActiveRootBindingIdAsync(pairId,
            SyncAuthorityCloudSides.LogicalLeft, cancellationToken);
        if (!string.Equals(retainedLeftRoot, leftRootBindingId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The private staging allocation may only reference the exact retained logical-left cloud root " +
                "authority of this pair " + pairId + ". The exact left root this preparation verified is not " +
                "the pair's own retained active logical-left root, so no staging allocation is created for a " +
                "foreign or wrong-side authority.");
        return new SyncControlNamespaceBinding(installationId, pairId,
            await allocator.BindAsync(anchor, installationId, pairId, retainedLeftRoot, policyRevision,
                Id("profile", BuildIdentity.SourceRevision, CapabilityJson), cancellationToken,
                side: SyncAuthorityCloudSides.LogicalLeft));
    }

    // -------------------------------------------------------------------------------------------------
    // CC4B-1/CC4B-3: the ONE cloud preparation route and the ONE construction factories
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// CC4B-3: the ONE cloud/cloud pair preparation route. The pair's own retained logical root binding ids
    /// are resolved from the retained rows - never from a caller claim, a pair id, a recovery id or a fake
    /// local endpoint - and handed to the exact CC4A prepare, which re-verifies both endpoints through their
    /// own bound accounts and reopens the pair's already completed private staging identity read-only. The
    /// returned context carries the typed two-port set, both retained side authorities, the private staging
    /// binding and the planner allocation options. No NTFS local root is opened anywhere on this route.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<PreparedPair> PrepareCloudPairAsync(SyncPair pair, CancellationToken cancellationToken)
    {
        stateAuthority.VerifyAuthority();
        var leftRoot = await _authority.LoadActiveRootBindingIdAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalLeft, cancellationToken);
        var rightRoot = await _authority.LoadActiveRootBindingIdAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalRight, cancellationToken);
        var prepared = await PrepareCloudPairBindingAsync(new CloudPairHostPrepareRequest(pair, leftRoot,
            rightRoot, ReplacingPairId: pair.Id, ExpectedPolicyRevision: pair.PolicyRevision, Generation: 1,
            CreatePrivateStaging: false, RequireConfigurationMutable: false), cancellationToken);
        // H2: this route is read-only runtime preparation for an UNCHANGED pair (a run, startup recovery or
        // endpoint verification), so it never takes the configuration-mutability guard. A genuine
        // configuration change still passes through the same guard on its own editing route.
        return new PreparedPair(prepared.Endpoints.Left.Store, prepared.Left.PairRootBindingId,
            prepared.Right.PairRootBindingId,
            prepared.PlannerOptions with { CloudAllocations = prepared.CloudAllocations }, CloudFiles: null,
            Cloud: prepared);
    }

    /// <summary>
    /// CC4B-1/CC4B-3: the ONE observer factory. Every observer of a pair is built here, so a cloud/cloud pair
    /// always gains its typed two-port set and is observed through the CC2C two-cloud component before any
    /// pair.LocalRoot open, while every legacy pair keeps byte-identical physical semantics because the
    /// optional port set stays null.
    /// </summary>
    private SyncAuthorityObservationCoordinator CreateObserver(SyncPair pair, PreparedPair prepared,
        Func<SyncComparisonProgress, Task>? onInventory = null)
    {
        LoadTransferSettings();
        return new(pair, prepared.Remote, _authority, prepared.LocalRootBindingId, prepared.DropboxRootBindingId,
            onInventory, prepared.Cloud?.Endpoints) { InventoryResources = _transferLimiter.Resources,
                ConcurrentProgressRetention = true };
    }

    /// <summary>
    /// CC4B-1/CC4B-3: the ONE runner factory. Every runner of a pair is built here, so a cloud/cloud pair
    /// always carries its typed context through the runner's one-way internal attach and no caller boolean
    /// can select a cloud engine. Legacy pairs keep the identical constructor and arguments.
    /// </summary>
    private SyncAuthorityPairRunner CreatePairRunner(SyncPair pair, PreparedPair prepared,
        PilotRunControl runControl, SyncAuthorityCommandAdmission admission, WindowsCloudFilesProvider? cloudFiles = null,
        IReadOnlyDictionary<string, string>? membershipObjectFences = null,
        IReadOnlyDictionary<string, string>? membershipPathFences = null,
        IReadOnlyDictionary<string, SyncAuthorityOperationKind>? expectedOperationKinds = null,
        Func<SyncComparisonProgress, Task>? onInventory = null,
        Func<SyncAuthorityLiveAction, Task>? onExecution = null,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null,
        IReadOnlyDictionary<string, SyncBootstrapMoveReview>? expectedMoveReviews = null)
    {
        // Every production consumer retains progress and updates the active run,
        // including recovery/conflict paths that have no presentation callback.
        onInventory ??= progress => NotifyProgressAsync(onProgress,
            new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: progress));
        onExecution ??= action => NotifyProgressAsync(onProgress,
            new SyncAuthorityBatchProgress(pair, "Running", null, null, null, Action: action));
        LoadTransferSettings();
        var runner = new SyncAuthorityPairRunner(pair, prepared.Remote, _authority, prepared.LocalRootBindingId,
            prepared.DropboxRootBindingId, prepared.Options, runControl, admission, cloudFiles, membershipObjectFences,
            membershipPathFences, expectedOperationKinds, onInventory, onExecution, expectedMoveReviews)
        {
            ConcurrentProgressRetention = true,
            InventoryResources = _transferLimiter.Resources,
            // Legacy scheduling has one scope for reads and mutations: reserve both lanes conservatively.
            TransferScheduling = _transferLimiter.Resources.ForOperations(
                (prepared.Cloud is { } ports
                    ? new[] { ports.Endpoints.Left.Store, ports.Endpoints.Right.Store }
                    : new[] { prepared.Remote }).OfType<ISyncTransferResourceEndpoint>()
                .SelectMany(endpoint => new[] {
                    new SyncProviderBudgetKey(endpoint.TransferResourceKey, SyncProviderOperation.Read),
                    new SyncProviderBudgetKey(endpoint.TransferResourceKey, SyncProviderOperation.Write) }),
                status => NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Running", null, null, null,
                    Comparison: new SyncComparisonProgress("Transfer resources", "<run>", status.ActiveFiles,
                        status.AllowedFiles, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                        status.Description, "files active"))))
        };
        if (prepared.Cloud is { } cloud)
            runner.AttachCloudContext(CreateCloudRunnerContext(cloud));
        return runner;
    }

    /// <summary>
    /// CC4B-1: the typed cloud context of one prepared pair plus the ONE way a cloud effect opens this pair's
    /// held private staging control root. The real state anchor is opened and verified per effect and released
    /// as soon as the pair's own completed control directory lease exists, which is the same per-call pattern
    /// the existing allocator already uses. A selected pair root is never opened or substituted here.
    /// </summary>
    private SyncAuthorityCloudRunnerContext CreateCloudRunnerContext(SyncAuthorityPreparedCloudPair prepared) =>
        new(prepared, () =>
        {
            using var anchor = PilotDirectoryLease.Open(stateAuthority.StateRoot);
            anchor.VerifyBinding();
            return SyncControlNamespace.OpenLocal(prepared.PrivateStaging, anchor);
        });

    /// <summary>
    /// CC4B-1: the ONE initial-planner factory. A cloud/cloud pair receives the same provider-aware comparer
    /// with the CC2F revision-bound common-digest resolver over its two typed ports that its reconciliation
    /// planner receives, so no planning call of a cloud pair omits it; a legacy pair keeps the legacy policy.
    /// </summary>
    private SyncAuthorityInitialPlanner CreateInitialPlanner(PreparedPair prepared) =>
        new(_authority, CloudContentComparison(prepared));

    /// <summary>The one shared provider-aware comparer of a prepared cloud pair, or null for a legacy pair.</summary>
    private SyncAuthorityProviderAwareContentComparison? CloudContentComparison(PreparedPair prepared) =>
        prepared.Cloud is { } cloud
            ? new SyncAuthorityProviderAwareContentComparison(cloud.Endpoints.Left.Provider,
                cloud.Endpoints.Right.Provider,
                new SyncAuthorityCloudCommonDigestResolver(_authority, cloud.Endpoints))
            : null;
    /// <summary>
    /// R1/H2: the exact unchanged-runtime predicate of the read-only preparation route. A runtime preparation
    /// (a run, startup recovery or endpoint verification) may omit the EDITING guard only while it proves it
    /// is preparing the pair exactly as it is already retained: the same retained pair identity, that pair's
    /// EXACT retained policy revision, this pair's own two exact retained root binding ids, and no creation of
    /// a new pair identity or of a new private staging allocation. Any deviation is refused here with no
    /// provider, root, staging or configuration effect, so the omitted editing guard can never be used to
    /// publish a changed pair or to create new authority from a runtime preparation. The configuration-edit
    /// route keeps its own EnsurePairConfigurationMutableAsync guard unchanged and is never reached through
    /// this predicate.
    /// </summary>
    private async Task RequireUnchangedRuntimePairAsync(CloudPairHostPrepareRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ReplacingPairId is null)
            throw new InvalidDataException(
                "Read-only runtime preparation requires the exact retained pair identity it prepares; a new " +
                "or unnamed pair is never prepared without the configuration-editing guard.");
        if (request.CreatePrivateStaging)
            throw new InvalidOperationException(
                "Read-only runtime preparation never creates or reallocates the private staging identity; " +
                "only the separate binding action does that. This request is refused without an effect.");
        var retained = await LoadPairsAsync(cancellationToken);
        var prior = retained.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, request.ReplacingPairId, StringComparison.Ordinal));
        if (prior is null)
            throw new InvalidDataException(
                "The pair this read-only runtime preparation names is not retained; publishing a new pair " +
                "requires the configuration-editing guard.");
        if (!string.Equals(prior.Id, request.Pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Read-only runtime preparation must prepare the retained pair itself; a different pair " +
                "identity is a configuration change and requires the editing guard.");
        if (request.ExpectedPolicyRevision is not { } expectedRevision)
            throw new InvalidDataException(
                "Read-only runtime preparation must name the exact retained policy revision it prepared; " +
                "an unnamed revision is refused rather than treated as unchanged.");
        if (expectedRevision != prior.PolicyRevision)
            throw new InvalidOperationException(
                "The pair changed after it was selected. Reload it before this run prepares it again.");
        if (!string.Equals(JsonSerializer.Serialize(SyncPairStore.NormalizePair(prior)),
                JsonSerializer.Serialize(SyncPairStore.NormalizePair(request.Pair)), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Read-only runtime preparation requires the exact retained pair configuration; changed " +
                "endpoints, accounts or policies require the configuration-editing guard.");
        var retainedLeft = await _authority.LoadActiveRootBindingIdAsync(prior.Id,
            SyncAuthorityCloudSides.LogicalLeft, cancellationToken);
        var retainedRight = await _authority.LoadActiveRootBindingIdAsync(prior.Id,
            SyncAuthorityCloudSides.LogicalRight, cancellationToken);
        if (!string.Equals(retainedLeft, request.LeftRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(retainedRight, request.RightRootBindingId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Read-only runtime preparation must use this pair's own retained root binding ids; a " +
                "changed, foreign or reversed root is a configuration change and requires the editing guard.");
    }

}
