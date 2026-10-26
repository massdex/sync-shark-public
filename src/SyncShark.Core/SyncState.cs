using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncPairStore
{
    private readonly SyncProductStateStore _store;

    public SyncPairStore(PilotStateAuthority authority)
    {
        _store = new SyncProductStateStore(authority);
    }

    public SyncPairSet Load()
    {
        return _store.Load().PairSet;
    }

    public string? LoadInstallationId()
    {
        var installationId = _store.Load().InstallationId;
        return string.IsNullOrWhiteSpace(installationId)
            ? null
            : SyncControlNamespace.NormalizeInstallationId(installationId);
    }

    public async Task<string> GetOrCreateInstallationIdAsync(CancellationToken cancellationToken = default)
    {
        string? installationId = null;
        await _store.UpdateAsync(current =>
        {
            installationId = current.InstallationId ?? Guid.NewGuid().ToString("N");
            installationId = SyncControlNamespace.NormalizeInstallationId(installationId);
            return current with { InstallationId = installationId };
        }, cancellationToken);
        return installationId!;
    }

    public Task SaveAsync(SyncPairSet set, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync(current =>
        {
            SyncProductStateStore.EnsureConfigurationMutable(current);
            var normalized = Normalize(set);
            return SyncProductStateStore.Normalize(current with
            {
                PairSet = normalized,
                Runtime = new SyncRuntimeState(current.Runtime.Pairs.Where(item =>
                    normalized.Pairs.Any(pair => string.Equals(pair.Id, item.PairId, StringComparison.Ordinal))).ToArray())
            });
        }, cancellationToken);

    public async Task<SyncPairSet> UpsertAsync(SyncPair pair, bool select = true, string? replacingPairId = null,
        CancellationToken cancellationToken = default)
    {
        pair = NormalizePair(pair);
        SyncPairSet? updated = null;
        await _store.UpdateAsync(product => SyncProductStateStore.Normalize(product with
        {
            PairSet = UpdatePairSet(product, pair, select, replacingPairId, out updated),
            Runtime = UpdateRuntimeForPairChange(product, pair, replacingPairId)
        }), cancellationToken);
        return updated!;
    }

    public async Task<SyncPairSet> SelectAsync(string pairId, CancellationToken cancellationToken = default)
    {
        SyncPairSet? updated = null;
        await _store.UpdateAsync(product =>
        {
            var current = product.PairSet;
            if (!current.Pairs.Any(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)))
                throw new InvalidDataException("The selected sync pair is not retained.");
            updated = current with { SelectedPairId = pairId };
            return SyncProductStateStore.Normalize(product with { PairSet = updated });
        }, cancellationToken);
        return updated!;
    }

    public async Task<SyncPairSet> RemoveAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pairId))
            throw new ArgumentException("The sync pair ID is required.", nameof(pairId));
        SyncPairSet? updated = null;
        await _store.UpdateAsync(product =>
        {
            SyncProductStateStore.EnsureConfigurationMutable(product);
            if (!product.PairSet.Pairs.Any(item => string.Equals(item.Id, pairId,
                    StringComparison.Ordinal)))
                throw new InvalidDataException("The sync pair selected for deletion is not retained.");
            var pairs = product.PairSet.Pairs.Where(item => !string.Equals(item.Id, pairId,
                    StringComparison.Ordinal)).ToArray();
            var selected = string.Equals(product.PairSet.SelectedPairId, pairId, StringComparison.Ordinal)
                ? pairs.FirstOrDefault()?.Id
                : product.PairSet.SelectedPairId;
            updated = Normalize(new SyncPairSet(pairs, selected));
            return SyncProductStateStore.Normalize(product with
            {
                PairSet = updated,
                Runtime = new SyncRuntimeState(product.Runtime.Pairs.Where(item =>
                    !string.Equals(item.PairId, pairId, StringComparison.Ordinal)).ToArray())
            });
        }, cancellationToken);
        return updated!;
    }

    private static SyncPairSet UpdatePairSet(SyncProductState product, SyncPair candidate, bool select,
        string? replacingPairId, out SyncPairSet updated)
    {
        SyncProductStateStore.EnsureConfigurationMutable(product);
        var current = product.PairSet;
        var replaced = replacingPairId is null
            ? current.Pairs.SingleOrDefault(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal))
            : current.Pairs.SingleOrDefault(item => string.Equals(item.Id, replacingPairId, StringComparison.Ordinal));
        if (replacingPairId is not null && replaced is null)
            throw new InvalidDataException("The sync pair selected for replacement is not retained.");

        if (replaced is not null && string.Equals(replaced.Id, candidate.Id, StringComparison.Ordinal))
        {
            var policyChanged = !SamePolicy(replaced, candidate);
            candidate = candidate with
            {
                PolicyRevision = policyChanged ? checked(replaced.PolicyRevision + 1) : replaced.PolicyRevision
            };
            if (candidate == replaced)
            {
                updated = current;
                return current;
            }
            var retained = current.Pairs.Select(item => string.Equals(item.Id, replaced.Id, StringComparison.Ordinal)
                    ? candidate
                    : item)
                .OrderByDescending(item => item.Priority).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            updated = Normalize(new SyncPairSet(retained, select ? candidate.Id : current.SelectedPairId));
            return updated;
        }

        if (current.Pairs.Any(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal)))
            throw new InvalidDataException("The replacement folder identity is already retained by another pair.");
        candidate = candidate with { PolicyRevision = 1 };
        var pairs = current.Pairs.Where(item => replaced is null || !string.Equals(item.Id, replaced.Id, StringComparison.Ordinal))
            .Append(candidate).OrderByDescending(item => item.Priority).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        updated = Normalize(new SyncPairSet(pairs, select ? candidate.Id : current.SelectedPairId));
        return updated;
    }

    private static SyncRuntimeState UpdateRuntimeForPairChange(SyncProductState product, SyncPair candidate,
        string? replacingPairId)
    {
        var replaced = replacingPairId is null
            ? product.PairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal))
            : product.PairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, replacingPairId, StringComparison.Ordinal));
        if (replaced is null || string.Equals(replaced.Id, candidate.Id, StringComparison.Ordinal))
            return product.Runtime;
        return new SyncRuntimeState(product.Runtime.Pairs.Where(item =>
            !string.Equals(item.PairId, replaced.Id, StringComparison.Ordinal)).ToArray());
    }

    private static bool SamePolicy(SyncPair left, SyncPair right) =>
        left.Enabled == right.Enabled && left.Priority == right.Priority &&
        left.Direction == right.Direction &&
        SyncDirectoryStoragePolicy.Default(left) == SyncDirectoryStoragePolicy.Default(right) &&
        SyncDirectoryStoragePolicy.Normalize(left.DirectoryStorageRules).SequenceEqual(SyncDirectoryStoragePolicy.Normalize(right.DirectoryStorageRules)) &&
        left.PropagateDeletions == right.PropagateDeletions &&
        left.FilesOnDemandEnabled == right.FilesOnDemandEnabled &&
        left.RecoveryRetentionDays == right.RecoveryRetentionDays &&
        left.SelectionDefaultIncluded == right.SelectionDefaultIncluded &&
        SyncIgnorePolicy.Normalize(left.IgnoreRules).SequenceEqual(SyncIgnorePolicy.Normalize(right.IgnoreRules)) &&
        SyncSelectionPolicy.Normalize(left.SelectionRules)
            .SequenceEqual(SyncSelectionPolicy.Normalize(right.SelectionRules));

    internal static SyncPairSet Normalize(SyncPairSet set) => Normalize(set, null);

    /// <summary>
    /// D2/OV637-4: the same pair-set normalization, with the caller's independently bound opaque cloud
    /// endpoint proofs threaded into the shared partition rule. A pair-set assembler that owns the exact
    /// retained authorities (the authority database reader) supplies a bound context so a legitimately
    /// retained path-opaque correspondence is admitted on the readback/Retain/Save path instead of being
    /// refused for want of a proof it already holds. Every other caller keeps the unchanged fail-closed
    /// behaviour by passing no proofs.
    /// </summary>
    internal static SyncPairSet Normalize(SyncPairSet set,
        Func<SyncPair, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>?>? proofsOf,
        Func<SyncPair, SyncPair>? comparisonOf = null)
    {
        var pairs = set.Pairs.Select(NormalizePair).ToArray();
        if (pairs.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != pairs.Length)
            throw new InvalidDataException("Retained sync pair IDs are not unique.");
        // A cloud/cloud record carries the absent (0,0) local identity, so it is excluded from the local
        // grouping; its actual endpoint identities are compared with the shared endpoint contract below.
        if (pairs.Where(item => item.LocalVolumeSerialNumber != 0 || item.LocalRootFileId != 0)
                .GroupBy(item => (item.LocalVolumeSerialNumber, item.LocalRootFileId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("One local directory identity cannot be retained by more than one pair.");
        if (pairs.GroupBy(item => (item.Provider, item.ConnectionId, item.DropboxFolderId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("One Dropbox folder identity cannot be retained by more than one pair.");
        // Pair-set uniqueness and overlap across every endpoint of every pair, for the cloud/cloud shape
        // whose absent local identity cannot participate in the grouping above.
        // EVERY retained pair is checked against every other retained pair through the one shared partition
        // rule, for every pair shape. A nested relation is admitted only when the enclosing pair's persisted
        // policy proves the whole nested subtree excluded, so a saved policy edit that removes a nested
        // exclusion or adds an included exception below it is refused here instead of silently invalidating an
        // admitted partition. Genuinely disjoint pairs are unaffected; an unverified relation is left to the
        // exact authority/hierarchy checks.
        // Only the authority-backed reader supplies comparison copies with each legacy pair's own
        // verified namespace. Raw definitions still own identity, policy, persistence and the return value.
        var comparisons = comparisonOf is null ? pairs : pairs.Select(comparisonOf).ToArray();
        for (var index = 0; index < pairs.Length; index++)
        {
            var validation = SyncPairPartitionPolicy.ValidateAgainstRetainedPairs(comparisons[index],
                comparisons.Where((_, other) => other != index), proofsOf);
            if (!validation.IsValid) throw new InvalidDataException(validation.Describe());
        }
        var selected = set.SelectedPairId;
        if (selected is not null && !pairs.Any(item => string.Equals(item.Id, selected, StringComparison.Ordinal)))
            throw new InvalidDataException("The selected sync pair is not present.");
        return new SyncPairSet(pairs, selected ?? pairs.FirstOrDefault()?.Id);
    }

    internal static SyncPair NormalizePair(SyncPair pair)
    {
        if (pair.Id.Length is < 8 or > 64 || pair.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("The sync pair ID is not accepted.");
        if (string.IsNullOrWhiteSpace(pair.Name) || pair.Name.Trim().Length > 80)
            throw new InvalidDataException("The sync pair name is not accepted.");
        if (pair.PolicyRevision < 1)
            throw new InvalidDataException("The sync pair policy revision is not accepted.");
        if (!Enum.IsDefined(pair.Direction)) throw new InvalidDataException("Unknown sync direction.");
        if (pair.DefaultStorage is { } storage && !Enum.IsDefined(storage)) throw new InvalidDataException("Unknown default storage mode.");
        // A validated cloud/cloud document is the one shape with no local identity: its compatibility
        // LocalRoot stays exactly empty and both numeric local fields stay exactly zero. It therefore
        // never passes through local-path normalization, the physical engine layout or ToPilotSettings.
        var cloudOnly = SyncCloudPairConfiguration.IsCloudOnly(pair);
        if (cloudOnly)
        {
            var endpoints = SyncCloudPairConfiguration.RequireProjection(pair);
            pair = pair with
            {
                LocalRoot = string.Empty,
                LocalVolumeSerialNumber = 0,
                LocalRootFileId = 0,
                DropboxRoot = endpoints.Right.FolderPath ?? string.Empty,
                DropboxFolderId = endpoints.Right.FolderId ?? string.Empty,
                Provider = SyncEndpoints.ProviderLiteral(endpoints.Right.Kind)!,
                ConnectionId = endpoints.Right.ConnectionId ?? string.Empty
            };
        }
        else if (pair.LocalVolumeSerialNumber == 0 || pair.LocalRootFileId == 0)
            throw new InvalidDataException("The saved local folder identity is incomplete.");
        var provider = SyncProviders.Normalize(pair.Provider);
        var localRight = provider == SyncProviders.Local;
        if (!cloudOnly && !SyncProviders.IsKnown(provider) && !localRight)
            throw new InvalidDataException("The saved pair provider is not supported.");
        if (!cloudOnly && (pair.EndpointDocument is not null || localRight))
            RequireEndpointProjection(pair, localRight);
        if (!localRight && (provider == SyncProviders.GoogleDrive
                ? !IsGoogleFolderIdentity(pair.DropboxFolderId)
                : !PilotPath.IsDropboxFolderIdentity(pair.DropboxFolderId)))
            throw new InvalidDataException("The saved cloud folder ID is incomplete for this provider.");
        var normalized = pair with
        {
            Name = pair.Name.Trim(),
            LocalRoot = cloudOnly ? string.Empty : PilotPath.NormalizeLocalRoot(pair.LocalRoot),
            DropboxRoot = localRight ? PilotPath.NormalizeLocalRoot(pair.DropboxRoot)
                : cloudOnly && string.IsNullOrWhiteSpace(pair.DropboxRoot)
                    ? string.Empty
                    : PilotPath.NormalizeDropboxRoot(pair.DropboxRoot),
            Priority = Math.Clamp(pair.Priority, -100, 100),
            SelectionRules = SyncSelectionPolicy.Normalize(pair.SelectionRules),
            IgnoreRules = SyncIgnorePolicy.Normalize(pair.IgnoreRules),
            DirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(pair.DirectoryStorageRules),
            DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Select(PilotPath.NormalizeRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ConnectionId = pair.ConnectionId?.Trim() ?? string.Empty,
            Provider = string.IsNullOrWhiteSpace(pair.Provider) ? "dropbox" : pair.Provider.Trim().ToLowerInvariant(),
            // The Dropbox Recovery-container path rule applies to Dropbox pairs only; a Google Drive
            // pair carries its own provider-scoped recovery folder identity instead.
            RecoveryRoot = pair.RecoveryRoot is null
                ? null
                : localRight
                    ? pair.RecoveryRoot with { RootPath = PilotPath.NormalizeLocalRoot(pair.RecoveryRoot.RootPath) }
                    : provider == SyncProviders.GoogleDrive
                    ? pair.RecoveryRoot with { RootPath = PilotPath.NormalizeDropboxRoot(pair.RecoveryRoot.RootPath) }
                    : pair.RecoveryRoot with
                    {
                        RootPath = SyncDropboxArtifactPaths.RequireRecoveryContainer(pair.RecoveryRoot.RootPath)
                    }
        };
        if (normalized.RecoveryRetentionDays is not (null or 30 or 90 or 180))
            throw new InvalidDataException("Recovery retention must be 30, 90, 180 days, or Never.");
        // The Dropbox Recovery-container path rule is provider-scoped: a Google Drive pair uses its own
        // recovery folder identity and must not be forced through a Dropbox artifact path.
        if (!localRight && normalized.RecoveryRoot is { } recovery &&
            ((provider == SyncProviders.GoogleDrive
                 ? !IsGoogleFolderIdentity(recovery.RootFolderId)
                 : !PilotPath.IsDropboxFolderIdentity(recovery.RootFolderId)) ||
             string.IsNullOrWhiteSpace(recovery.RootNamespaceId)))
            throw new InvalidDataException("The saved cloud Recovery folder identity is incomplete.");
        if (cloudOnly)
        {
            // There is no local root to protect. A right cloud endpoint that carries a provider path keeps
            // the existing remote-container rule. An opaque provider's empty presentation path carries no
            // authority and is not passed to a path normalizer here; the live root, cross-pair and recovery
            // containment checks remain mandatory in the Host activation work before any sync effect.
            if (!string.IsNullOrWhiteSpace(normalized.DropboxRoot))
                SyncControlNamespace.RequireRemotePairRootAllowed(normalized.DropboxRoot);
        }
        else
        {
            SyncControlNamespace.RequireLocalPairRootAllowed(normalized.LocalRoot);
            if (localRight) SyncControlNamespace.RequireLocalPairRootAllowed(normalized.DropboxRoot);
            else SyncControlNamespace.RequireRemotePairRootAllowed(normalized.DropboxRoot);
        }
        if (normalized.ControlNamespace is not null)
        {
            SyncControlNamespace.Validate(normalized.ControlNamespace);
            if (!string.Equals(normalized.ControlNamespace.NamespaceId, normalized.Id, StringComparison.Ordinal))
                throw new InvalidDataException("The control namespace is not bound to the sync pair identity.");
        }
        return normalized;
    }

    private static void RequireEndpointProjection(SyncPair pair, bool localRight)
    {
        if (pair.EndpointDocument is null)
            throw new InvalidDataException("A two-local-folder pair needs both explicit endpoint identities.");
        var resolved = SyncEndpointPairRuntime.Resolve(pair, pair.EndpointDocument);
        if (!resolved.IsResolved) throw new InvalidDataException(resolved.Validation.Describe());
        var endpoints = resolved.Endpoints!;
        var layout = SyncEndpointEngineLayout.Create(endpoints);
        if (localRight)
        {
            if (!endpoints.Left.IsLocal || !endpoints.Right.IsLocal || resolved.RightLocal is not { } right ||
                !string.Equals(right.RootPath, PilotPath.NormalizeLocalRoot(pair.DropboxRoot), StringComparison.OrdinalIgnoreCase) ||
                right.IdentityToken != pair.DropboxFolderId || !string.IsNullOrEmpty(pair.ConnectionId))
                throw new InvalidDataException("The right local endpoint disagrees with the retained runtime binding.");
            if (pair.FilesOnDemandEnabled)
                throw new InvalidDataException("A pair of local folders does not have a cloud Files On Demand root.");
            if (pair.RightControlNamespace is { } control)
            {
                SyncControlNamespace.Validate(control);
                if (control.NamespaceId != SyncEndpointPairRuntime.RightControlNamespaceId(pair.Id) ||
                    pair.ControlNamespace is not null && control.InstallationId != pair.ControlNamespace.InstallationId)
                    throw new InvalidDataException("The right private namespace is not bound to this pair and installation.");
            }
        }
        else if (!layout.Remote.IsCloud ||
            SyncEndpoints.ProviderLiteral(layout.Remote.Kind) != SyncProviders.Normalize(pair.Provider) ||
            layout.Remote.ConnectionId != pair.ConnectionId || layout.Remote.FolderId != pair.DropboxFolderId ||
            layout.Remote.FolderPath != pair.DropboxRoot)
            throw new InvalidDataException("The cloud endpoint disagrees with the retained runtime binding.");
    }

    // Google folder IDs are opaque stable object identities, not Dropbox's id: namespace.
    // The root alias is resolved by the browser before binding; paths and URLs are not IDs.
    private static bool IsGoogleFolderIdentity(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 1024 && id != "root" &&
        !id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
            character is '/' or '\\' or '?' or '#');
}

public sealed class SyncRuntimeStateStore
{
    private readonly SyncProductStateStore _store;

    public SyncRuntimeStateStore(PilotStateAuthority authority) =>
        _store = new SyncProductStateStore(authority);

    public SyncRuntimeState Load() => _store.Load().Runtime;

    public SyncPairRuntimeState LoadPair(string pairId) => Load().Pairs.SingleOrDefault(item =>
        string.Equals(item.PairId, pairId, StringComparison.Ordinal)) ?? new SyncPairRuntimeState(pairId, false, [], null);

    public IReadOnlyList<string> LoadRecoveryOrder()
    {
        var product = _store.Load();
        var ordered = new List<string>();
        if (product.ActiveRun is { } active) ordered.Add(active.PairId);
        ordered.AddRange(product.Runtime.Pairs
            .Where(item => item.PendingTransfer is not null && !ordered.Contains(item.PairId, StringComparer.Ordinal))
            .OrderBy(item => item.PendingTransfer!.StartedUtc)
            .ThenBy(item => item.PairId, StringComparer.Ordinal)
            .Select(item => item.PairId));
        return ordered;
    }

    public Task<SyncRunLease> BeginRunAsync(string pairId, long policyRevision,
        CancellationToken cancellationToken = default) =>
        _store.BeginRunAsync(pairId, policyRevision, cancellationToken);

    public Task EndRunAsync(SyncRunLease lease, CancellationToken cancellationToken = default) =>
        _store.EndRunAsync(lease, cancellationToken);

    public async Task SavePairAsync(SyncPairRuntimeState pairState, string? runGeneration = null,
        CancellationToken cancellationToken = default)
    {
        pairState = NormalizePair(pairState);
        await _store.UpdateAsync(product =>
        {
            if (!product.PairSet.Pairs.Any(item => string.Equals(item.Id, pairState.PairId, StringComparison.Ordinal)))
                throw new InvalidDataException("Runtime state cannot be retained for an unknown sync pair.");
            if (product.ActiveRun is { } active)
            {
                if (!string.Equals(active.PairId, pairState.PairId, StringComparison.Ordinal) ||
                    !string.Equals(active.Generation, runGeneration, StringComparison.Ordinal))
                    throw new InvalidOperationException("Runtime mutation is not bound to the persisted active run generation.");
            }
            else if (runGeneration is not null)
            {
                throw new InvalidOperationException("Runtime mutation supplied a run generation but no run is active.");
            }
            var pairs = product.Runtime.Pairs.Where(item => !string.Equals(item.PairId, pairState.PairId, StringComparison.Ordinal))
                .Append(pairState).OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray();
            return SyncProductStateStore.Normalize(product with { Runtime = Normalize(new SyncRuntimeState(pairs)) });
        }, cancellationToken);
    }

    internal static SyncRuntimeState Normalize(SyncRuntimeState state)
    {
        var pairs = state.Pairs.Select(NormalizePair).ToArray();
        if (pairs.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() != pairs.Length)
            throw new InvalidDataException("Runtime pair state IDs are not unique.");
        return new SyncRuntimeState(pairs);
    }

    internal static SyncPairRuntimeState NormalizePair(SyncPairRuntimeState state)
    {
        if (string.IsNullOrWhiteSpace(state.PairId)) throw new InvalidDataException("Runtime pair state has no pair ID.");
        var files = state.Files.Select(item => item with { RelativePath = PilotPath.NormalizeRelative(item.RelativePath) })
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Runtime file baseline contains duplicate paths.");
        var initialObservation = (state.InitialObservation ?? []).Select(item => item with
            { RelativePath = PilotPath.NormalizeRelative(item.RelativePath) })
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (initialObservation.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != initialObservation.Length)
            throw new InvalidDataException("The initial discovery observation contains duplicate paths.");
        if (state.BaselineEstablished && (initialObservation.Length != 0 || state.InitialObservationCount != 0) ||
            !state.BaselineEstablished && state.InitialObservationCount is < 0 or > 2)
            throw new InvalidDataException("The initial discovery phase is inconsistent.");
        if (state.PendingTransfer is { } pending)
        {
            if (!string.Equals(pending.PairId, state.PairId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(pending.Id))
                throw new InvalidDataException("Pending transfer identity is inconsistent.");
            if (pending.CreationPolicyRevision < 1)
                throw new InvalidDataException("Pending transfer has no accepted creation-policy revision.");
            PilotPath.NormalizeRelative(pending.RelativePath);
            if (!Enum.IsDefined(pending.Kind) || !Enum.IsDefined(pending.Phase))
                throw new InvalidDataException("Pending operation kind or phase is not accepted.");
            var localTransfer = pending.Direction == SyncDirection.Download &&
                                pending.Kind is SyncIntentKind.Transfer or SyncIntentKind.ResolveKeepDropbox;
            if (localTransfer)
            {
                var safeCreateNew = SyncCapabilityGate.IsSafeCreateNewIntent(pending);
                var privateReplacement = SyncCapabilityGate.IsPrivateReplacementIntent(pending);
                if (!safeCreateNew && !privateReplacement && (pending.StageFileName != SyncLocalTransactionDirectory.StageName ||
                    pending.BackupFileName is not (null or SyncLocalTransactionDirectory.BackupName) ||
                    pending.TransactionDirectoryName is null || pending.TransactionDirectoryId is null ||
                    pending.TransactionDirectorySecurityHash is null ||
                    pending.TransactionDirectorySecurityHash.Length != 64 ||
                    pending.TransactionDirectorySecurityHash.Any(value => !Uri.IsHexDigit(value))))
                    throw new InvalidDataException("A local transfer has no accepted private transaction binding.");
                if (!safeCreateNew && !privateReplacement)
                    SyncLocalRecovery.ParseSourceIdentity(pending.TransactionDirectoryId!);
            }
            else if (pending.TransactionDirectoryName is not null || pending.TransactionDirectoryId is not null ||
                     pending.TransactionDirectorySecurityHash is not null || pending.ArtifactsBound ||
                     pending.StageArtifactId is not null || pending.BackupArtifactId is not null)
                throw new InvalidDataException("A non-local-transfer intent carries private transaction state.");
            if (pending.Kind == SyncIntentKind.Transfer && pending.Direction == SyncDirection.Upload)
            {
                if (pending.ExpectedSourceVolumeSerialNumber is null or 0 || pending.ExpectedSourceFileId is null or 0 ||
                    pending.ExpectedSourceLastWriteUtc is null || pending.ExpectedSourceSize is null ||
                    string.IsNullOrWhiteSpace(pending.ExpectedSourceId) ||
                    SyncLocalRecovery.ParseSourceIdentity(pending.ExpectedSourceId) !=
                    new PilotDirectoryIdentity(pending.ExpectedSourceVolumeSerialNumber.Value,
                        pending.ExpectedSourceFileId.Value))
                    throw new InvalidDataException("An upload intent has no exact local source identity.");
                var replacement = pending.ExpectedDestinationRevision is not null;
                if (replacement && (string.IsNullOrWhiteSpace(pending.ExpectedDestinationRemoteId) ||
                                    pending.ExpectedDestinationSize is null ||
                                    string.IsNullOrWhiteSpace(pending.RecoveryPath)))
                    throw new InvalidDataException("A replacement upload has no exact Dropbox predecessor and recovery destination.");
            }
            if (pending.Kind == SyncIntentKind.Transfer && pending.Direction == SyncDirection.Download &&
                (string.IsNullOrWhiteSpace(pending.ExpectedSourceId) ||
                 string.IsNullOrWhiteSpace(pending.ExpectedSourceRevision) || pending.ExpectedSourceSize is null))
                throw new InvalidDataException("A download intent has no exact Dropbox source identity.");
            if (pending.Phase is SyncIntentPhase.CommittedCleanupPending or SyncIntentPhase.CleanupCompleted &&
                (pending.Direction != SyncDirection.Download ||
                 pending.StageFileName is null && !SyncCapabilityGate.IsPrivateReplacementIntent(pending) ||
                 pending.Kind is not (SyncIntentKind.Transfer or SyncIntentKind.ResolveKeepDropbox)))
                throw new InvalidDataException("A cleanup-pending intent is not bound to a local download artifact set.");
            if (pending.StageArtifactId is not null)
            {
                if (pending.StageFileName is null && pending.StagingRelativePath is null)
                    throw new InvalidDataException("A stage artifact identity has no bound name.");
                SyncLocalRecovery.ParseSourceIdentity(pending.StageArtifactId);
            }
            if (pending.BackupArtifactId is not null)
            {
                if (pending.BackupFileName is null && !SyncCapabilityGate.IsPrivateReplacementIntent(pending))
                    throw new InvalidDataException("A backup artifact identity has no bound name.");
                SyncLocalRecovery.ParseSourceIdentity(pending.BackupArtifactId);
            }
            if (!pending.ArtifactsBound && !SyncCapabilityGate.IsPrivateReplacementIntent(pending) &&
                !SyncCapabilityGate.IsSafeCreateNewIntent(pending) &&
                (pending.StageArtifactId is not null || pending.BackupArtifactId is not null))
                throw new InvalidDataException("An unbound private transaction carries artifact identities.");
            if (pending.Kind != SyncIntentKind.Transfer)
            {
                if (string.IsNullOrWhiteSpace(pending.RecoveryPath) || string.IsNullOrWhiteSpace(pending.RecoveryOriginalPath) ||
                    !string.Equals(PilotPath.NormalizeRelative(pending.RecoveryOriginalPath),
                        pending.RelativePath, StringComparison.Ordinal) ||
                    pending.ExpectedSourceSize is null or < 0)
                    throw new InvalidDataException("Pending recovery intent is not bound to an exact destination, source path and size.");
                if (string.IsNullOrWhiteSpace(pending.ExpectedSourceId))
                    throw new InvalidDataException("Pending recovery intent has no exact source object identity.");
                if (pending.Kind == SyncIntentKind.ArchiveRemoteAfterLocalDeletion &&
                    string.IsNullOrWhiteSpace(pending.ExpectedSourceRevision))
                    throw new InvalidDataException("Pending Dropbox recovery intent has no exact source revision.");
                if (pending.Kind == SyncIntentKind.ResolveKeepLocal &&
                    (string.IsNullOrWhiteSpace(pending.ExpectedDestinationRemoteId) ||
                     string.IsNullOrWhiteSpace(pending.ExpectedDestinationRevision) ||
                     pending.ExpectedDestinationSize is null))
                    throw new InvalidDataException("Conflict recovery has no exact Dropbox predecessor identity.");
                if (pending.Kind == SyncIntentKind.ArchiveLocalAfterDropboxDeletion)
                    SyncLocalRecovery.ParseSourceIdentity(pending.ExpectedSourceId);
                if (pending.Kind == SyncIntentKind.ResolveKeepDropbox &&
                    (pending.ExpectedDestinationVolumeSerialNumber is null or 0 ||
                     pending.ExpectedDestinationFileId is null or 0 || pending.ExpectedDestinationSize is null))
                    throw new InvalidDataException("Conflict recovery has no exact local predecessor identity.");
                var hasRecoveryReadback = pending.RecoveryFileId is not null ||
                                          pending.RecoveryContentHash is not null || pending.RecoverySize is not null;
                if (hasRecoveryReadback)
                {
                    var expectedRecoveryHash = pending.Kind is SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox
                        ? pending.ExpectedDestinationContentHash
                        : pending.SourceContentHash;
                    var expectedRecoverySize = pending.Kind is SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox
                        ? pending.ExpectedDestinationSize
                        : pending.ExpectedSourceSize;
                    if (string.IsNullOrWhiteSpace(pending.RecoveryFileId) ||
                        string.IsNullOrWhiteSpace(pending.RecoveryContentHash) ||
                        pending.RecoverySize != expectedRecoverySize ||
                        !string.Equals(pending.RecoveryContentHash, expectedRecoveryHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Pending recovery readback is incomplete or differs from the prebound source.");
                    if (pending.Kind is SyncIntentKind.ArchiveLocalAfterDropboxDeletion or SyncIntentKind.ResolveKeepDropbox)
                        SyncLocalRecovery.ParseSourceIdentity(pending.RecoveryFileId);
                }
                if (pending.Phase is SyncIntentPhase.ArchiveCompleted or SyncIntentPhase.CommittedCleanupPending &&
                    (string.IsNullOrWhiteSpace(pending.RecoveryFileId) ||
                     string.IsNullOrWhiteSpace(pending.RecoveryContentHash) ||
                     pending.RecoverySize != (pending.Kind is SyncIntentKind.ResolveKeepLocal or SyncIntentKind.ResolveKeepDropbox
                         ? pending.ExpectedDestinationSize
                         : pending.ExpectedSourceSize)))
                    throw new InvalidDataException("Completed recovery intent has no exact retained-object readback.");
                if (pending.Phase is SyncIntentPhase.ArchiveCompleted or SyncIntentPhase.CommittedCleanupPending &&
                    pending.Kind is SyncIntentKind.ArchiveRemoteAfterLocalDeletion or SyncIntentKind.ResolveKeepLocal &&
                    string.IsNullOrWhiteSpace(pending.RecoveryRevision))
                    throw new InvalidDataException("Completed Dropbox recovery intent has no retained revision.");
            }
            if (pending.Kind == SyncIntentKind.Transfer && pending.Direction == SyncDirection.Upload &&
                pending.RecoveryPath is not null)
            {
                var hasRecovery = pending.RecoveryFileId is not null || pending.RecoveryRevision is not null ||
                                  pending.RecoveryContentHash is not null || pending.RecoverySize is not null;
                if (hasRecovery && (string.IsNullOrWhiteSpace(pending.RecoveryFileId) ||
                                    string.IsNullOrWhiteSpace(pending.RecoveryRevision) ||
                                    !string.Equals(pending.RecoveryContentHash, pending.ExpectedDestinationContentHash,
                                        StringComparison.OrdinalIgnoreCase) ||
                                    pending.RecoverySize != pending.ExpectedDestinationSize))
                    throw new InvalidDataException("A replacement upload recovery readback is incomplete or mismatched.");
                if (pending.Phase is SyncIntentPhase.ArchiveCompleted or SyncIntentPhase.SuccessorCreated && !hasRecovery)
                    throw new InvalidDataException("A replacement upload advanced without durable predecessor recovery proof.");
            }
            if (pending.Phase == SyncIntentPhase.SuccessorCreated &&
                (string.IsNullOrWhiteSpace(pending.SuccessorFileId) ||
                 string.IsNullOrWhiteSpace(pending.SuccessorRevision) ||
                 string.IsNullOrWhiteSpace(pending.SuccessorContentHash) || pending.SuccessorSize is null))
                throw new InvalidDataException("A successor-created intent has no exact successor identity.");
        }
        return state with { Files = files, InitialObservation = initialObservation };
    }
}

internal sealed class SyncProductStateStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ActiveLocalRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly DualSlotJsonStore<SyncProductState> _store;
    private readonly DualSlotJsonStore<SyncPairSet> _legacyPairs;
    private readonly DualSlotJsonStore<SyncRuntimeState> _legacyRuntime;
    private readonly PilotSettingsStore _legacySettings;
    private readonly object _gate;
    private readonly string _stateKey;

    public SyncProductStateStore(PilotStateAuthority authority)
    {
        _store = new DualSlotJsonStore<SyncProductState>(authority, SyncSharkRuntimePaths.ProductStateName);
        _legacyPairs = new DualSlotJsonStore<SyncPairSet>(authority, SyncSharkRuntimePaths.PairSetStateName);
        _legacyRuntime = new DualSlotJsonStore<SyncRuntimeState>(authority, SyncSharkRuntimePaths.RuntimeStateName);
        _legacySettings = new PilotSettingsStore(authority);
        _stateKey = authority.StateRoot;
        _gate = Gates.GetOrAdd(_stateKey, static _ => new object());
    }

    public SyncProductState Load()
    {
        lock (_gate) return LoadCore();
    }

    public Task UpdateAsync(Func<SyncProductState, SyncProductState> update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = LoadCore();
            var updated = Normalize(update(current));
            if (JsonSerializer.SerializeToUtf8Bytes(updated).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current))) return Task.CompletedTask;
            _store.SaveAsync(updated, cancellationToken).GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    public Task<SyncRunLease> BeginRunAsync(string pairId, long policyRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (ActiveLocalRuns.ContainsKey(_stateKey))
                throw new InvalidOperationException("A sync run already owns the local aggregate state.");
            var current = LoadCore();
            var pair = current.PairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("A run cannot start for an unknown sync pair.");
            if (pair.PolicyRevision != policyRevision)
                throw new InvalidOperationException("The sync pair policy changed before the run lease was acquired.");
            if (current.ActiveRun is { } abandoned && !string.Equals(abandoned.PairId, pairId, StringComparison.Ordinal))
                throw new InvalidOperationException("Another pair has an interrupted run lease that must be reconciled first.");
            var lease = new SyncRunLease(pairId, Guid.NewGuid().ToString("N"), policyRevision, DateTimeOffset.UtcNow);
            _store.SaveAsync(Normalize(current with { ActiveRun = lease }), cancellationToken).GetAwaiter().GetResult();
            if (!ActiveLocalRuns.TryAdd(_stateKey, lease.Generation))
                throw new InvalidOperationException("The process-local run fence could not be acquired.");
            return Task.FromResult(lease);
        }
    }

    public Task EndRunAsync(SyncRunLease lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            try
            {
                var current = LoadCore();
                if (current.ActiveRun is null ||
                    !string.Equals(current.ActiveRun.PairId, lease.PairId, StringComparison.Ordinal) ||
                    !string.Equals(current.ActiveRun.Generation, lease.Generation, StringComparison.Ordinal))
                    throw new InvalidOperationException("The persisted run lease changed before terminal release.");
                _store.SaveAsync(Normalize(current with { ActiveRun = null }), cancellationToken).GetAwaiter().GetResult();
            }
            finally
            {
                ActiveLocalRuns.TryRemove(new KeyValuePair<string, string>(_stateKey, lease.Generation));
            }
        }
        return Task.CompletedTask;
    }

    private SyncProductState LoadCore() => Normalize(_store.LoadOrCreate(CreateMigratedState));

    private SyncProductState CreateMigratedState()
    {
        var pairSet = _legacyPairs.Load();
        if (pairSet is null)
        {
            var legacy = _legacySettings.Load();
            if (legacy is not null)
            {
                var pair = SyncPairStore.NormalizePair(SyncPair.FromPilot(legacy));
                pairSet = new SyncPairSet([pair], pair.Id);
            }
        }
        pairSet ??= new SyncPairSet([], null);
        var retained = pairSet.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var runtime = new SyncRuntimeState((_legacyRuntime.Load() ?? new SyncRuntimeState([])).Pairs
            .Where(item => retained.Contains(item.PairId)).ToArray());
        return Normalize(new SyncProductState(pairSet, runtime, InstallationId: Guid.NewGuid().ToString("N")));
    }

    internal static SyncProductState Normalize(SyncProductState state)
    {
        var pairSet = SyncPairStore.Normalize(state.PairSet);
        var retained = pairSet.Pairs.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var runtime = SyncRuntimeStateStore.Normalize(state.Runtime);
        if (runtime.Pairs.Any(item => !retained.Contains(item.PairId)))
            throw new InvalidDataException("Aggregate runtime state refers to a sync pair that is not retained in the same generation.");
        foreach (var runtimePair in runtime.Pairs)
        {
            var pair = pairSet.Pairs.Single(item => string.Equals(item.Id, runtimePair.PairId, StringComparison.Ordinal));
            if (runtimePair.PendingTransfer is { } pending && pending.CreationPolicyRevision > pair.PolicyRevision)
                throw new InvalidDataException("Pending transfer creation policy is newer than its retained pair policy.");
        }
        if (state.ActiveRun is { } active)
        {
            var pair = pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, active.PairId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The active run lease refers to an unknown sync pair.");
            if (active.Generation.Length != 32 || active.Generation.Any(character => !char.IsAsciiHexDigit(character)) ||
                active.PolicyRevision != pair.PolicyRevision)
                throw new InvalidDataException("The active run lease generation or policy revision is not accepted.");
        }
        var installationId = state.InstallationId is null
            ? null
            : SyncControlNamespace.NormalizeInstallationId(state.InstallationId);
        if (pairSet.Pairs.Any(item => item.ControlNamespace is not null &&
                                     !string.Equals(item.ControlNamespace.InstallationId, installationId, StringComparison.Ordinal)))
            throw new InvalidDataException("A sync pair control namespace belongs to another installation identity.");
        // One shared Rewind normalizer serves creation, import and the authority reader, so a malformed
        // retained session can never become an empty active session or permission to resume.
        var rewindSessions = SyncRewindState.NormalizeAll(state.RewindSessions ?? [], retained);
        // After publication the aggregate retains only the bounded storage reference bound to the
        // authority installation that owns the normalized rows. A collection that is both non-empty and
        // marked as published is contradictory retained state and fails closed instead of silently
        // choosing one copy.
        var rewindStorage = state.RewindStorage is null
            ? null
            : SyncRewindStorageReference.Normalize(state.RewindStorage, installationId);
        if (rewindStorage is not null && rewindSessions.Count != 0)
            throw new InvalidDataException(
                "The retained Rewind aggregate still contains sessions that are published to the authority database.");
        return new SyncProductState(pairSet, runtime, state.ActiveRun, installationId,
            SyncTransferSettings.Normalize(state.TransferSettings), rewindSessions, rewindStorage);
    }

    internal static void EnsureConfigurationMutable(SyncProductState state)
    {
        if (state.ActiveRun is not null)
            throw new InvalidOperationException("Pair configuration is fenced while a sync run is active.");
        if (state.Runtime.Pairs.Any(item => item.PendingTransfer is not null))
            throw new InvalidOperationException("Pair configuration is fenced until pending recovery is reconciled.");
    }
}

// The one explicit Rewind accessor over the authority database. It holds no state of its own beyond the
// installation identity the rows are bound to, so no reader can cache an active-session absence as
// execution authority and no second producer of Rewind state exists in the product-state aggregate.
public sealed class SyncRewindStore
{
    private readonly SyncAuthorityDatabase _authority;
    private string? _installationId;

    public SyncRewindStore(SyncAuthorityDatabase authority) =>
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public Task<IReadOnlyList<SyncRewindSession>> LoadAsync(CancellationToken cancellationToken = default) =>
        _authority.LoadRewindSessionsAsync(cancellationToken);

    public Task<SyncRewindSession?> LoadActiveAsync(string pairId, CancellationToken cancellationToken = default) =>
        _authority.LoadActiveRewindSessionAsync(pairId, cancellationToken);

    // Materialize once before a loop; never one whole-session read per pair.
    public Task<IReadOnlySet<string>> LoadActivePairIdsAsync(CancellationToken cancellationToken = default) =>
        _authority.LoadActiveRewindPairIdsAsync(cancellationToken);

    public Task<bool> MatchesAsync(SyncRewindSession session, CancellationToken cancellationToken = default) =>
        _authority.RewindSessionMatchesAsync(session, cancellationToken);

    public async Task UpsertAsync(SyncRewindSession session, CancellationToken cancellationToken = default) =>
        await _authority.PublishRewindSessionAsync(await RequireInstallationIdAsync(cancellationToken), session,
            cancellationToken);

    public async Task PublishPreviewAsync(SyncRewindSession session, string? expectedSessionId,
        string? expectedLocalPreviewScanId, string? expectedDropboxPreviewScanId,
        CancellationToken cancellationToken = default) =>
        await _authority.PublishRewindPreviewAsync(await RequireInstallationIdAsync(cancellationToken), session,
            expectedSessionId, expectedLocalPreviewScanId, expectedDropboxPreviewScanId, cancellationToken);

    public Task PersistStateAsync(string sessionId, string pairId, SyncRewindStatus status, string detail,
        string? operationGroupId, bool clearOperationGroup, CancellationToken cancellationToken = default) =>
        _authority.PersistRewindSessionStateAsync(sessionId, pairId, status, detail, operationGroupId,
            clearOperationGroup, cancellationToken);

    public Task PersistPathCheckpointAsync(string sessionId, string pairId, string localPreviewScanId,
        string dropboxPreviewScanId, string? operationGroupId, SyncAuthorityReviewedPathCheckpoint checkpoint,
        SyncRewindStatus status, string detail, CancellationToken cancellationToken = default) =>
        _authority.PersistRewindPathCheckpointAsync(sessionId, pairId, localPreviewScanId, dropboxPreviewScanId,
            operationGroupId, checkpoint, status, detail, cancellationToken);

    private async Task<string> RequireInstallationIdAsync(CancellationToken cancellationToken) =>
        _installationId ??= await _authority.LoadAuthorityInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException("The authority database installation identity is unavailable.");
}

public sealed class SyncTransferSettingsStore
{
    private readonly SyncProductStateStore _store;

    public SyncTransferSettingsStore(PilotStateAuthority authority) =>
        _store = new SyncProductStateStore(authority);

    public SyncTransferSettings Load() => SyncTransferSettings.Normalize(_store.Load().TransferSettings);

    public Task SaveAsync(SyncTransferSettings settings, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync(current =>
        {
            SyncProductStateStore.EnsureConfigurationMutable(current);
            return current with { TransferSettings = SyncTransferSettings.Normalize(settings) };
        }, cancellationToken);
}

public sealed class SyncHistoryStore
{
    private readonly DualSlotJsonStore<SyncHistory> _store;

    public SyncHistoryStore(PilotStateAuthority authority) =>
        _store = new DualSlotJsonStore<SyncHistory>(authority, SyncSharkRuntimePaths.HistoryStateName);

    public SyncHistory Load() => Normalize(_store.Load() ?? new SyncHistory([]));

    public async Task AppendRunAsync(string pairName, SyncRunResult result, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var additions = result.Decisions.Select(item => new SyncHistoryEntry(now, result.PairId, pairName,
            item.RelativePath, item.Kind, item.Reason));
        var updated = new SyncHistory(additions.Concat(Load().Entries).ToArray());
        await _store.SaveAsync(Normalize(updated), cancellationToken);
    }

    private static SyncHistory Normalize(SyncHistory history)
    {
        var entries = history.Entries.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.PairId) || string.IsNullOrWhiteSpace(item.PairName))
                throw new InvalidDataException("Sync history pair identity is incomplete.");
            var path = item.RelativePath == "<run>" ? item.RelativePath : PilotPath.NormalizeRelative(item.RelativePath);
            if (item.Reason.Length > 1_000) throw new InvalidDataException("Sync history reason exceeds its bounded length.");
            return item with { PairName = item.PairName.Trim(), RelativePath = path };
        }).OrderByDescending(item => item.OccurredUtc).ToArray();
        return new SyncHistory(entries);
    }
}

public sealed class SyncAutomationStore
{
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 24 * 60;
    private readonly DualSlotJsonStore<SyncAutomationSettings> _store;

    public SyncAutomationStore(PilotStateAuthority authority) =>
        _store = new DualSlotJsonStore<SyncAutomationSettings>(authority, SyncSharkRuntimePaths.AutomationStateName);

    public SyncAutomationSettings Load() => Normalize(_store.Load() ?? new SyncAutomationSettings(true, 15, false));

    public Task SaveAsync(SyncAutomationSettings settings, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(Normalize(settings), cancellationToken);

    private static SyncAutomationSettings Normalize(SyncAutomationSettings settings)
    {
        if (settings.IntervalMinutes is < MinimumIntervalMinutes or > MaximumIntervalMinutes)
            throw new InvalidDataException($"The sync interval must be between {MinimumIntervalMinutes} and {MaximumIntervalMinutes} minutes.");
        // RunOnStartup was the pilot's redundant "run once when this window opens" switch.
        // Startup now always performs recovery first, while continuous work is controlled by
        // Automatic syncing and Windows logon launch. Keep the serialized member only so old
        // v0.2.4 settings deserialize additively, but normalize it out of active policy.
        return settings with { RunOnStartup = false };
    }
}
