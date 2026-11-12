using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private sealed record CloudRecoveryConsumer(SyncAuthorityCloudRecoveryDescriptor Descriptor,
        SyncAuthorityAllocation Allocation, SyncAuthorityCloudRootIdentity Endpoint,
        SyncAuthorityCloudRecoveryRoot Recovery, CloudSideResolution Side)
    {
        internal SyncRemoteRecoveryRootBinding Binding =>
            new(Recovery.DisplayPath, Recovery.FolderId, Recovery.NamespaceId
                ?? throw new InvalidDataException("The Recovery namespace is unavailable."));
    }

    // Shared by metadata and effect paths. Metadata does not contact providers, allocate staging,
    // prepare a pair, or repair a missing binding. The effect path adds fresh provider readback.
    private async Task<CloudRecoveryConsumer> ResolveCloudRecoveryAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, SyncAuthorityCloudRecoveryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var (allocation, endpoint) = await _authority.LoadCloudRecoveryConsumerBindingAsync(
            artifact, descriptor, cancellationToken);
        var context = SyncEndpointContext.Capture(pair);
        var captured = new[] { context.Left, context.Right }.SingleOrDefault(side =>
            side.CompatibilityPort == descriptor.LogicalSide && !side.IsLocal)
            ?? throw new InvalidDataException("This pair no longer has the Recovery copy's original cloud endpoint.");
        if (captured.Provider != endpoint.Provider || captured.NamespaceId != endpoint.NamespaceId ||
            captured.FolderId != endpoint.FolderId || captured.ConnectionId is null)
            throw new InvalidDataException("This pair was reconfigured; its Recovery copy is not redirected to the new endpoint.");
        var record = new SyncEndpointRecord(captured.Kind, Provider: captured.Provider,
            ConnectionId: captured.ConnectionId, NamespaceId: captured.NamespaceId,
            FolderId: captured.FolderId, FolderPath: captured.FolderPath);
        var side = ResolveCloudSide(record);
        if (side.Facts.ProviderAccountId != descriptor.ProviderAccountId)
            throw new InvalidDataException("The Recovery copy's original provider account is unavailable.");
        var installation = await _authority.LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidDataException("The retained installation identity is unavailable.");
        var recovery = await _authority.FindCloudRecoveryRootAsync(installation, descriptor.Provider,
            descriptor.RecoveryProviderAccountId, descriptor.RecoveryNamespaceId,
            descriptor.RecoveryFolderId, descriptor.RecoveryCorpusId, cancellationToken)
            ?? throw new InvalidDataException("The exact retained Recovery root is unavailable.");
        if (recovery.RecoveryRootBindingId != descriptor.RecoveryRootBindingId ||
            recovery.DisplayPath != descriptor.RecoveryPath ||
            recovery.RecoveryRootBindingId == descriptor.EndpointRootBindingId)
            throw new InvalidDataException("The Recovery copy disagrees with its retained root binding.");
        return new CloudRecoveryConsumer(descriptor, allocation, endpoint, recovery, side);
    }

    private static bool RecoveryCleanupEligible(SyncPair pair, SyncRecoveryArtifactProjection artifact) =>
        pair.RecoveryRetentionDays is null ||
        artifact.RetainedUtc.AddDays(pair.RecoveryRetentionDays.Value) <= DateTimeOffset.UtcNow;

    private static string RecoveryRetentionLabel(SyncPair pair, SyncRecoveryArtifactProjection artifact) =>
        pair.RecoveryRetentionDays is null ? "Kept until you remove it" :
            $"Protected until {artifact.RetainedUtc.AddDays(pair.RecoveryRetentionDays.Value):yyyy-MM-dd}; cleanup is manual";

    private async Task<SyncRecoveryCatalogItem> RetainedRecoveryCatalogItemAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, CancellationToken cancellationToken)
    {
        SyncAuthorityCloudRecoveryDescriptor? descriptor = null;
        try
        {
            if (!SyncAuthorityCloudRecoveryDescriptor.TryParse(artifact.IdentityJson, out descriptor))
            {
                RequireRecoveryBrowserSupported(pair);
                var legacyAccount = await RecoveryAccountDisplayAsync(pair, cancellationToken);
                return ToRecoveryCatalogItem(pair, legacyAccount, artifact);
            }
            var resolved = await ResolveCloudRecoveryAsync(pair, artifact, descriptor!, cancellationToken);
            var account = LoadAccountBinding(resolved.Side.Facts.ConnectionId);
            var display = account is null ? resolved.Side.Facts.ConnectionId :
                account.Email + " (" + account.DisplayName + ")";
            return CloudRecoveryCatalogItem(pair, artifact, descriptor!, display,
                "Recorded; the exact cloud copy is verified again before an action",
                RecoveryCleanupEligible(pair, artifact), resolved.Side.Facts.ConnectionId,
                // CC4E correction: the exact typed cloud file route this resolution just bound is the one
                // restorable route, and only while the artifact is still retained. A released, rebound,
                // disconnected or otherwise unresolvable item stays visible and disabled with its own reason.
                // This remains presentation only: the action revalidates every authority from the item's Key.
                canRestore: string.Equals(artifact.Status, "retained", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      ArgumentException or NotSupportedException)
        {
            // A broken explicit descriptor or disconnected endpoint remains visible. It must not
            // fall through to the historical physical 'dropbox' bucket or borrow another account.
            return CloudRecoveryCatalogItem(pair, artifact, descriptor, "Original account unavailable",
                "Blocked: " + error.Message, false, null);
        }
    }

    private static SyncRecoveryCatalogItem CloudRecoveryCatalogItem(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, SyncAuthorityCloudRecoveryDescriptor? descriptor,
        string account, string health, bool canRemove, string? connectionId, bool canRestore = false) =>
        new(artifact.RecoveryArtifactId, pair.Id, pair.Name, SyncRecoverySource.DropboxArchive,
            descriptor?.OriginalPath ?? artifact.OriginalRelativePath, account,
            descriptor?.RetainedPath ?? "Recorded location unavailable", artifact.Size, artifact.RetainedUtc,
            RecoveryRetentionLabel(pair, artifact), health, artifact.ContentHash, descriptor?.RetainedFileId,
            descriptor?.RetainedRevision, artifact.IdentityJson, canRestore, "file", canRemove,
            descriptor?.Provider, descriptor?.LogicalSide, connectionId);

    private async Task VerifyCloudRecoveryScopeAsync(CloudRecoveryConsumer resolved,
        CancellationToken cancellationToken)
    {
        var recoveryIdentity = resolved.Recovery.ToRootIdentity()
            ?? throw new InvalidDataException("The retained Recovery root has no native identity.");
        var recoveryHierarchy = await ReadRetainedAuthorityHierarchyAsync(recoveryIdentity, cancellationToken);
        if (!recoveryHierarchy.CompleteToCorpusRoot ||
            recoveryHierarchy.CorpusRootId != resolved.Descriptor.RecoveryCorpusId)
            throw new InvalidDataException("The Recovery root's provider corpus changed.");
        var endpointHierarchy = await ReadCandidateHierarchyAsync(resolved.Side,
            resolved.Endpoint.AuthorityId, cancellationToken);
        if (SyncAuthorityCloudRootComparison.Compare(resolved.Endpoint, endpointHierarchy,
                recoveryIdentity, recoveryHierarchy) != SyncAuthorityCloudRootRelation.Distinct)
            throw new InvalidDataException("The endpoint and Recovery root are no longer verified separate folders.");
        // A moved Recovery root must not silently become a different pair's live data folder.
        foreach (var other in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            if (other.AuthorityId == recoveryIdentity.AuthorityId || other.AuthorityId == resolved.Endpoint.AuthorityId ||
                other.Provider != recoveryIdentity.Provider) continue;
            var hierarchy = await ReadRetainedAuthorityHierarchyAsync(other, cancellationToken);
            if (SyncAuthorityCloudRootComparison.Compare(recoveryIdentity, recoveryHierarchy, other, hierarchy)
                != SyncAuthorityCloudRootRelation.Distinct)
                throw new InvalidDataException("The Recovery root overlaps another retained authority or its location cannot be verified.");
        }
        await resolved.Side.Adapter.VerifyRecoveryRootAsync(resolved.Binding, cancellationToken);
    }

    private static void RequireExactCloudRecoveryMember(CloudRecoveryConsumer resolved, SyncArchiveResult member)
    {
        var descriptor = resolved.Descriptor;
        if (member.FileId != descriptor.RetainedFileId || member.Revision != descriptor.RetainedRevision ||
            member.RecoveryPath != descriptor.RetainedPath || member.Size != descriptor.Size ||
            !string.Equals(member.ContentHash, descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Recovery path now contains a different object or revision; it was left unchanged.");
    }

    private async Task<SyncRecoveryCleanupResult> RemoveTypedCloudRecoveryAsync(SyncPair pair,
        SyncRecoveryArtifactProjection artifact, SyncAuthorityCloudRecoveryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (artifact.Status == "released")
            return new SyncRecoveryCleanupResult(pair.Id, artifact.RecoveryArtifactId, "The recovery copy was already removed.");
        var resuming = artifact.Status == "release_authorized";
        if (!resuming && !RecoveryCleanupEligible(pair, artifact))
            throw new InvalidOperationException("This Recovery copy is still protected from cleanup.");
        var resolved = await ResolveCloudRecoveryAsync(pair, artifact, descriptor, cancellationToken);
        await VerifyCloudRecoveryScopeAsync(resolved, cancellationToken);
        var member = await resolved.Side.Adapter.GetRecoveryAsync(resolved.Binding,
            resolved.Allocation.DeterministicName, cancellationToken);
        if (member is null && !resuming)
            throw new InvalidDataException("The retained Recovery copy is missing; no release was authorized.");
        if (member is not null) RequireExactCloudRecoveryMember(resolved, member);
        if (!resuming)
            await _authority.AuthorizeRecoveryArtifactReleaseAsync(artifact.RecoveryArtifactId, cancellationToken);
        if (member is not null)
        {
            await resolved.Side.Adapter.DeleteRecoveryAsync(resolved.Binding, member, cancellationToken);
            var readback = await resolved.Side.Adapter.GetRecoveryAsync(resolved.Binding,
                resolved.Allocation.DeterministicName, cancellationToken);
            if (readback is not null)
                throw new InvalidDataException("The cloud did not confirm that the exact Recovery copy is absent; release remains unresolved.");
        }
        await _authority.MarkRecoveryArtifactReleasedAsync(artifact.RecoveryArtifactId, cancellationToken);
        return new SyncRecoveryCleanupResult(pair.Id, artifact.RecoveryArtifactId,
            "The selected Recovery copy was removed. Its activity record remains available.");
    }

    // =====================================================================================================
    // CC4E-1/CC4E-2: the ONE shared manual cloud restore helper
    //
    // One explicit user command restores or copies one CC3C typed cloud Recovery artifact through the SAME
    // private staging component, the SAME operation journal, lease, allocation and attempt machinery and the
    // SAME adapter upload surface the cloud update engine already uses. It is not a second restore engine and
    // it never adopts the historical delete-then-copy physical assumption: the exact retained archive bytes
    // are read through the target's own bound Recovery root into one bounded private stage, and the destination
    // is then written conditionally - create-only while it is absent, or a revision-bound replacement whose
    // exact current version was retained in that same endpoint's own Recovery root first. Preview and effect
    // both resolve their authority from the retained rows, never from the caller's item, its CanRestore flag,
    // a pair provider or a JSON assertion.
    // =====================================================================================================

    /// <summary>One resolved typed cloud restore route: the retained artifact and its exact descriptor.</summary>
    private sealed record CloudRestoreRoute(SyncRecoveryArtifactProjection Artifact,
        SyncAuthorityCloudRecoveryDescriptor Descriptor);

    /// <summary>
    /// The exact outcome of one typed cloud restore: the destination the effect actually wrote (a copy
    /// re-derives its own create-only destination at effect time) and the operator-facing detail.
    /// </summary>
    private sealed record CloudRestoreOutcome(string DestinationPath, string Detail);

    /// <summary>
    /// The bounded in-process dispatch/mutation seam used by the focused manual-restore proofs. It is an
    /// ordinary in-process hook with no production default, exactly like the shared cloud engines' own phase
    /// seams; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? CloudRestorePhaseForTest { get; set; }

    /// <summary>
    /// CC4E closure (item A proof): a default-off per-test shortening of the bounded abandonment window.
    /// It exists ONLY so the expiry path can be proved quickly; it never replaces the real cleanup or the
    /// real database decision, and the production value stays the 30-second bound.
    /// </summary>
    internal static TimeSpan AbandonCleanupWindowForTest { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// One fully resolved typed cloud restore: the retained authority, the target's own live typed port and
    /// retained side authority, the exact normalized destination, the destination as it currently exists (or
    /// null when it is absent) and whether this call restores a copy.
    /// </summary>
    private sealed record CloudRestoreTarget(SyncRecoveryArtifactProjection Artifact,
        SyncAuthorityCloudRecoveryDescriptor Descriptor, CloudRecoveryConsumer Consumer,
        SyncAuthorityCloudEndpointPort Port, SyncAuthorityCloudSideAuthority Side,
        SyncAuthorityPreparedCloudPair Cloud, string DestinationPath, PilotRemoteEntry? Current,
        bool RestoreAsCopy)
    {
        internal string PairId => Artifact.PairId;

        internal bool LeftSide => string.Equals(Descriptor.LogicalSide,
            SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);

        internal string SideLabel => LeftSide ? "left" : "right";

        internal string ObjectId => Id("object-cloud-restore", PairId, Descriptor.LogicalSide,
            Descriptor.EndpointRootBindingId, DestinationPath);

        internal SyncArchiveResult RetainedMember => new(Descriptor.RetainedFileId,
            Descriptor.RetainedRevision!, Descriptor.RetainedPath, Descriptor.Size, Descriptor.ContentHash!);

        internal SyncRemoteRecoveryRootBinding RecoveryBinding => Consumer.Binding;

        /// <summary>
        /// The exact allocation shape this restore needs, taken from the existing shared allocation policy: an
        /// absent destination is allocated exactly like a create target and an existing one exactly like an
        /// update target, so a replacement carries its own remote recovery allocation while a copy does not.
        /// </summary>
        internal SyncAuthorityOperationKind AllocationKind => Current is null
            ? LeftSide ? SyncAuthorityOperationKind.CreateLocal : SyncAuthorityOperationKind.CreateRemote
            : LeftSide ? SyncAuthorityOperationKind.UpdateLocal : SyncAuthorityOperationKind.UpdateRemote;
    }

    /// <summary>
    /// Resolves the typed route for one requested recovery key: the exact retained artifact of this pair whose
    /// identity is an explicit cloud descriptor. A genuine legacy identity (no explicit descriptor kind)
    /// returns null so the historical physical route keeps its exact meaning; a malformed or explicitly
    /// unsupported descriptor throws and is never silently downgraded to the legacy route.
    /// </summary>
    private async Task<CloudRestoreRoute?> ResolveTypedCloudRestoreRouteAsync(SyncPair pair, string key,
        CancellationToken cancellationToken)
    {
        var artifact = (await _authority.LoadRecoveryArtifactsAsync(cancellationToken)).SingleOrDefault(item =>
                           string.Equals(item.RecoveryArtifactId, key, StringComparison.Ordinal) &&
                           string.Equals(item.PairId, pair.Id, StringComparison.Ordinal) && item.Side == "dropbox");
        // A key that names no retained recovery artifact of this pair is not a typed cloud recovery copy at all
        // (a provider revision-catalog key, for example). The historical route then applies its own exact
        // retained-artifact, revision and destination checks exactly as it always did; a matching artifact row
        // whose identity is an explicit unsupported descriptor is still refused below and never falls back.
        if (artifact is null) return null;
        if (!SyncAuthorityCloudRecoveryDescriptor.TryParse(artifact.IdentityJson, out var descriptor))
            return null;
        if (!string.Equals(artifact.Status, "retained", StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "This cloud recovery copy is not in its retained state (" + artifact.Status +
                "); it is refused without an effect and without a legacy fallback.");
        return new CloudRestoreRoute(artifact, descriptor!);
    }

    /// <summary>
    /// Resolves one typed cloud restore completely from retained authority: the CC4C consumer join, the
    /// verified endpoint/Recovery hierarchy, this pair's exact current typed cloud context, the exact
    /// normalized destination - chosen as an absent create-only copy path through the target's own adapter
    /// when a copy was requested - and the destination as it currently exists. Every disagreement refuses with
    /// no effect: a caller's item, its CanRestore flag, its provider or its identity JSON is never authority.
    /// </summary>
    private async Task<CloudRestoreTarget> ResolveCloudRestoreTargetAsync(SyncPair pair, CloudRestoreRoute route,
        bool restoreAsCopy, CancellationToken cancellationToken, string? admittedDestination = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cloud Recovery requires the Windows Sync Shark runtime.");
        var descriptor = route.Descriptor;
        var consumer = await ResolveCloudRecoveryAsync(pair, route.Artifact, descriptor, cancellationToken);
        await VerifyCloudRecoveryScopeAsync(consumer, cancellationToken);
        // The exact retained archive is re-established through the target's own bound Recovery root BEFORE
        // anything else is prepared, so a moved, changed or unreachable retained copy refuses here instead of
        // after an operation and its private allocations already exist.
        await RequireExactCloudRetainedCopyAsync(consumer, descriptor, cancellationToken);
        var prepared = await PrepareCloudPairAsync(pair, cancellationToken);
        var cloud = prepared.Cloud ?? throw new SyncAuthorityCloudRecoveryException(
            "A typed cloud Recovery copy belongs to an explicit cloud/cloud pair; this pair has no typed cloud context, so nothing was restored.");
        var port = cloud.Port(descriptor.LogicalSide);
        var side = cloud.SideAuthority(descriptor.LogicalSide);
        if (!string.Equals(port.RootBindingId, descriptor.EndpointRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(side.PairRootBindingId, descriptor.EndpointRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(port.Provider, descriptor.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(side.Provider, descriptor.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(port.ProviderAccountId, descriptor.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(side.ProviderAccountId, descriptor.ProviderAccountId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained Recovery copy's original endpoint is not this pair's exact current logical endpoint, provider account and pair root; nothing was restored.");
        if (!string.Equals(side.RecoveryRootBindingId, descriptor.RecoveryRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(side.RecoveryNamespaceId, descriptor.RecoveryNamespaceId, StringComparison.Ordinal) ||
            !string.Equals(side.RecoveryFolderId, descriptor.RecoveryFolderId, StringComparison.Ordinal) ||
            !string.Equals(side.RecoveryCorpusId, descriptor.RecoveryCorpusId, StringComparison.Ordinal) ||
            !string.Equals(side.RecoveryPath, descriptor.RecoveryPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained Recovery copy does not live under this logical endpoint's own current retained Recovery root; nothing was restored.");
        if (!string.Equals(descriptor.NativeAlgorithm, port.Definition.ContentHashAlgorithm,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained Recovery copy was not recorded with this provider's registered native algorithm; nothing was restored.");
        var destination = admittedDestination is not null
            ? PilotPath.NormalizeRelative(admittedDestination)
            : restoreAsCopy
                ? await FindAvailableCloudRecoveryCopyPathAsync(port, descriptor.OriginalPath, cancellationToken)
                : PilotPath.NormalizeRelative(descriptor.OriginalPath);
        if (!restoreAsCopy && destination != PilotPath.NormalizeRelative(descriptor.OriginalPath))
            throw new SyncAuthorityCloudRecoveryException(
                "The admitted original restore destination differs from the retained artifact.");
        var current = await ReadCloudRestoreDestinationAsync(port, destination, cancellationToken);
        if (current is not null && current.Kind != PilotEntryKind.File)
            throw new SyncAuthorityCloudRecoveryException(
                "An unsupported object occupied the requested restore destination; nothing was restored and nothing was deleted.");
        if (restoreAsCopy && current is not null)
            throw new SyncAuthorityCloudRecoveryException(
                "The chosen copy destination is no longer absent; no copy was issued and no existing object was replaced.");
        if (!restoreAsCopy && current is not null &&
            (string.IsNullOrWhiteSpace(current.Id) || string.IsNullOrWhiteSpace(current.Revision) ||
             string.IsNullOrWhiteSpace(current.ContentHash) || current.Size < 0))
            throw new SyncAuthorityCloudRecoveryException(
                "The current restore destination has no exact identity, revision, size and native digest to retain before replacement; nothing was restored and nothing was deleted.");
        return new CloudRestoreTarget(route.Artifact, descriptor, consumer, port, side, cloud, destination,
            current, restoreAsCopy);
    }

    /// <summary>
    /// The one exact retained-copy pre-check of this manual restore: the target adapter's own bound recovery
    /// lookup must still return this exact retained member - native identity, revision, recovery path, size and
    /// digest - at the retained Recovery root. A moved, changed, released or unavailable copy refuses before
    /// any operation, allocation, stage or provider effect exists for it.
    /// </summary>
    private static async Task RequireExactCloudRetainedCopyAsync(CloudRecoveryConsumer consumer,
        SyncAuthorityCloudRecoveryDescriptor descriptor, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(descriptor.RetainedPath);
        if (string.IsNullOrWhiteSpace(name))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained Recovery descriptor carries no exact recovery member name; nothing was restored.");
        var retained = await consumer.Side.Adapter.GetRecoveryAsync(consumer.Binding, name, cancellationToken);
        if (retained is null || string.IsNullOrWhiteSpace(retained.FileId) ||
            string.IsNullOrWhiteSpace(retained.Revision) || string.IsNullOrWhiteSpace(retained.ContentHash))
            throw new SyncAuthorityCloudRecoveryException(
                "The exact retained Recovery copy is not present under its recorded bound Recovery root; nothing was restored and no legacy route was used.");
        if (!string.Equals(retained.FileId, descriptor.RetainedFileId, StringComparison.Ordinal) ||
            !string.Equals(retained.Revision, descriptor.RetainedRevision, StringComparison.Ordinal) ||
            !string.Equals(retained.RecoveryPath, descriptor.RetainedPath, StringComparison.Ordinal) ||
            retained.Size != descriptor.Size ||
            !string.Equals(retained.ContentHash, descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained Recovery copy changed or moved after it was recorded; it was left unchanged and nothing was restored.");
    }

    /// <summary>
    /// CC4E-1: the typed cloud preview. It resolves the same retained authority the effect path uses and names
    /// the actual provider, account, logical endpoint, the exact retained copy and the exact normalized
    /// destination, which for a copy is one create-only path proven absent through that target's own adapter.
    /// </summary>
    private async Task<SyncRecoveryEffectPreview> PreviewTypedCloudRecoveryAsync(SyncPair pair,
        SyncRecoveryCatalogItem item, CloudRestoreRoute route, bool restoreAsCopy,
        CancellationToken cancellationToken)
    {
        var target = await ResolveCloudRestoreTargetAsync(pair, route, restoreAsCopy, cancellationToken);
        var account = LoadAccountBinding(target.Consumer.Side.Facts.ConnectionId);
        var display = account is null ? target.Consumer.Side.Facts.ConnectionId :
            account.Email + " (" + account.DisplayName + ")";
        var preservation = target.Current is null || target.RestoreAsCopy
            ? "No current destination is displaced."
            : "The current destination is retained in this endpoint's own Recovery copy before replacement.";
        return new SyncRecoveryEffectPreview(item.PairId, item.Key, item.Source, route.Descriptor.OriginalPath,
            target.DestinationPath, restoreAsCopy, target.Current is not null,
            $"Restore the retained {target.Port.Definition.DisplayName} recovery copy of '{route.Descriptor.OriginalPath}' for {display} at the {target.SideLabel} cloud endpoint to {(restoreAsCopy ? "a new copy" : "its original path")}. The exact retained copy, its original provider account, this endpoint's own Recovery root, the destination and the target's conditional write are verified again before any effect. {preservation}");
    }

    /// <summary>
    /// CC4E-2: the typed cloud restore/copy effect. It stages the exact retained archive bytes privately, then
    /// writes the destination conditionally - create-only while it is absent, or after retaining the exact
    /// current destination version in the target's own Recovery root - and completes only when the adapter's
    /// own known-success receipt and an independent destination readback both agree. An unsupported conditional
    /// write is a durably recorded zero-effect refusal; an unknown or mismatching outcome keeps the command
    /// blocked and the operation unresolved with its stage, preimage and receipts retained, and no reissue.
    /// </summary>
    private async Task<CloudRestoreOutcome> RestoreTypedCloudRecoveryAsync(SyncPair pair, CloudRestoreRoute route,
        bool restoreAsCopy, string admittedDestination, string commandId,
        SyncAuthorityCommandAdmission admission, CancellationToken cancellationToken)
    {
        // Recheck the accepted destination's live absence/identity; a second time-derived
        // copy name would no longer represent this command's immutable target.
        var target = await ResolveCloudRestoreTargetAsync(pair, route, restoreAsCopy, cancellationToken,
            admittedDestination);
        var descriptor = target.Descriptor;
        var cloud = target.Cloud;
        var port = target.Port;
        var options = cloud.PlannerOptions with { CloudAllocations = cloud.CloudAllocations };
        var idempotencyKey = Id("idempotency", Id("operation", commandId, "cloud-recovery-restore", descriptor.LogicalSide,
            descriptor.EndpointRootBindingId, target.DestinationPath));
        var actionRegistration = await _authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
        var operationId = actionRegistration.OperationId;
        var objectId = target.ObjectId;
        var destinationExpected = target.Current is null ? (object?)null : new
        {
            fileId = target.Current.Id,
            revision = target.Current.Revision,
            size = target.Current.Size,
            contentHash = target.Current.ContentHash
        };
        var preconditions = JsonSerializer.Serialize(new
        {
            action = "restore_recovery",
            restoreKind = "cloud-recovery-restore",
            kind = SyncAuthorityNames.OperationKind(SyncAuthorityOperationKind.Reconcile),
            pairId = pair.Id,
            path = target.DestinationPath,
            configurationRevision = options.ConfigurationRevision,
            restoreCommandId = commandId,
            recoveryArtifactId = route.Artifact.RecoveryArtifactId,
            logicalSide = descriptor.LogicalSide,
            provider = descriptor.Provider,
            providerAccountId = descriptor.ProviderAccountId,
            endpointRootBindingId = descriptor.EndpointRootBindingId,
            recoveryRootBindingId = descriptor.RecoveryRootBindingId,
            recoveryNamespaceId = descriptor.RecoveryNamespaceId,
            recoveryFolderId = descriptor.RecoveryFolderId,
            recoveryCorpusId = descriptor.RecoveryCorpusId,
            recoveryPath = descriptor.RecoveryPath,
            retainedPath = descriptor.RetainedPath,
            retainedFileId = descriptor.RetainedFileId,
            retainedRevision = descriptor.RetainedRevision,
            retainedSize = descriptor.Size,
            retainedContentHash = descriptor.ContentHash,
            nativeAlgorithm = descriptor.NativeAlgorithm,
            restoreAsCopy,
            destinationAbsent = target.Current is null,
            destination = destinationExpected
        });
        var members = new[]
        {
            new SyncAuthorityMember(Id("member", operationId, "affected"), pair.Id, objectId, "affected",
                descriptor.EndpointRootBindingId, null, null, null, target.DestinationPath,
                JsonSerializer.Serialize(new
                {
                    present = target.Current is not null,
                    path = target.DestinationPath,
                    restoreAsCopy,
                    sourceRecoveryArtifactId = route.Artifact.RecoveryArtifactId
                })),
            new SyncAuthorityMember(Id("member", operationId, "recovery"), pair.Id, objectId, "recovery",
                descriptor.EndpointRootBindingId, null, null, null, descriptor.RetainedPath,
                JsonSerializer.Serialize(new
                {
                    present = true,
                    recoveryArtifactId = route.Artifact.RecoveryArtifactId,
                    recoveryRootBindingId = descriptor.RecoveryRootBindingId,
                    recoveryCorpusId = descriptor.RecoveryCorpusId,
                    path = descriptor.RetainedPath,
                    retainedFileId = descriptor.RetainedFileId,
                    retainedRevision = descriptor.RetainedRevision,
                    retainedSize = descriptor.Size,
                    retainedContentHash = descriptor.ContentHash
                }))
        };
        var allocations = SyncAuthorityOperationAllocations.Build(operationId, target.AllocationKind,
            target.DestinationPath, cloud.Left.PairRootBindingId, cloud.Right.PairRootBindingId, "file",
            descriptor.Size, options);
        // The restore's own logical object is registered first, exactly as the shared conflict-copy plan does:
        // the operation and its members reference it, and no existing object identity is rewritten by it.
        await _authority.RegisterObjectAsync(objectId, "file", cancellationToken);
        await _authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
            idempotencyKey, SyncAuthorityOperationKind.Reconcile, pair.Id, objectId,
            options.ConfigurationRevision, options.CapabilityProfileId, preconditions, 100, members,
            Array.Empty<SyncAuthorityDependency>(), allocations), admission, cancellationToken,
            reservation: actionRegistration.Reservation);
        await using var leaseRenewal = await _authority.BeginFencedOperationLeaseRenewalAsync(operationId, commandId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        CloudRestorePhaseForTest?.Invoke("operation-leased-before-stage");
        using var anchor = PilotDirectoryLease.Open(stateAuthority.StateRoot);
        anchor.VerifyBinding();
        using var controlRoot = SyncControlNamespace.OpenLocal(cloud.PrivateStaging, anchor);
        var context = new SyncAuthorityCloudExecutionContext(cloud.Endpoints, cloud.PrivateStaging, controlRoot);
        SyncAuthorityCloudContentStage? stage = null;
        var leaseReleased = false;
        try
        {
            var operation = await _authority.LoadOperationAsync(operationId, cancellationToken);
            RequireCloudRestorePlan(operation, target, commandId);
            stage = await SyncAuthorityCloudContentStage.PrepareRecoveryReadAsync(_authority, lease, port,
                target.RecoveryBinding, target.RetainedMember, objectId, route.Artifact.RecoveryArtifactId,
                descriptor.RetainedPath, descriptor.NativeAlgorithm, cloud.PrivateStaging, controlRoot,
                cancellationToken);
            var targetDigest = await stage.ComputeNativeDigestAsync(port.Provider, cancellationToken);
            // The destination is re-established immediately before the request boundary. A destination that
            // appeared, disappeared or changed since it was verified refuses here, before any request.
            var current = await ReadCloudRestoreDestinationAsync(port, target.DestinationPath, cancellationToken);
            if (current is not null && current.Kind != PilotEntryKind.File)
                throw new SyncAuthorityCloudRecoveryException(
                    "An unsupported object now occupies the restore destination; no request was issued, nothing was replaced and nothing was deleted.");
            if (target.RestoreAsCopy && current is not null)
                throw new SyncAuthorityCloudRecoveryException(
                    "The copy destination is no longer absent; no request was issued and no existing object was replaced.");
            if (!SameCloudRestorePredecessor(current, target.Current))
                throw new SyncAuthorityCloudRecoveryException(
                    "The destination no longer matches the exact state this restore verified - it appeared, disappeared or changed since then; no request was issued, the current version was not overwritten and nothing was deleted.");
            SyncAuthorityAllocation? recoveryAllocation = null;
            if (current is not null)
            {
                recoveryAllocation = operation.Allocations.SingleOrDefault(allocation =>
                                        allocation.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile)
                                    ?? throw new SyncAuthorityCloudRecoveryException(
                                        "The retained restore plan carries no remote recovery allocation for the destination it must retain; no request was issued.");
                var preimage = await SyncAuthorityCloudRecovery.EnsureRestorePreimageAsync(_authority, port,
                    target.Side, recoveryAllocation, current, operation, target.DestinationPath,
                    port.Definition.ContentHashAlgorithm, cloud.PrivateStaging.NamespaceId, lease,
                    cancellationToken);
                if (preimage.Refusal is not null)
                    throw new SyncAuthorityCloudRecoveryException(
                        "The exact current destination was not retained in this endpoint's own Recovery copy: " +
                        preimage.Refusal.Reason + " No request was issued and the destination is unchanged.");
            }
            stage.VerifyHeld();
            var destinationState = current is null ? "absent" : current.Id + "|" + current.Revision + "|" +
                current.Size.ToString(CultureInfo.InvariantCulture) + "|" + current.ContentHash;
            var fingerprint = SyncAuthorityCloudExecution.Hash("cloud-recovery-restore", pair.Id,
                descriptor.RecoveryRootBindingId, descriptor.RetainedFileId, descriptor.RetainedRevision!,
                descriptor.RetainedPath, descriptor.Size.ToString(CultureInfo.InvariantCulture),
                target.DestinationPath, destinationState, stage.NativeHash, targetDigest.Digest,
                target.RestoreAsCopy ? "copy" : "in-place");
            var attempt = await _authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
                port.Provider + (current is null ? "-recovery-restore-create" : "-recovery-restore-replace"),
                cancellationToken);
            CloudRestorePhaseForTest?.Invoke("request-dispatched-before-upload");
            PilotUploadResult uploaded;
            try
            {
                stage.ResetPosition();
                uploaded = await port.Store.UploadAsync(target.DestinationPath, stage.Content,
                    targetDigest.Digest, current?.Revision, cancellationToken);
            }
            catch (Exception error) when (SyncAuthorityCloudExecution.ConclusiveNonApplication(error))
            {
                await RecordCloudRestoreNotAppliedAsync(attempt, lease, port, stage, fingerprint,
                    target.DestinationPath, error, cancellationToken);
                throw new SyncAuthorityCloudRecoveryException(
                    "The target adapter cannot perform this conditional restore at all; the destination was not changed, the exact current version and the retained recovery copy are preserved and the refused effect is durably recorded.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordCloudRestoreUnknownAsync(attempt, lease, port, stage, fingerprint,
                    target.DestinationPath, error, cancellationToken);
                throw new SyncAuthorityCloudRecoveryException(
                    "The conditional restore response is unknown; the command stays blocked, the destination preimage and the private stage are retained and no second request is issued. Reconcile this exact operation before any retry.");
            }
            CloudRestorePhaseForTest?.Invoke("after-upload-before-readback");
            PilotRemoteEntry successor;
            try
            {
                successor = await VerifyCloudRestoreSuccessorAsync(port, target.DestinationPath, stage,
                    targetDigest, uploaded, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordCloudRestoreUnknownAsync(attempt, lease, port, stage, fingerprint,
                    target.DestinationPath, error, cancellationToken);
                throw new SyncAuthorityCloudRecoveryException(
                    "The restore response and its independent destination readback did not establish one exact successor; the command stays blocked, the stage and preimage are retained and no request was reissued. " + error.Message);
            }
            var destinationJson = JsonSerializer.Serialize(new
            {
                path = target.DestinationPath,
                successor.Id,
                successor.Revision,
                successor.Size,
                successor.ContentHash,
                targetNativeAlgorithm = targetDigest.Algorithm
            });
            var requestEvidence = JsonSerializer.Serialize(new
            {
                restoreCommandId = commandId,
                recoveryArtifactId = route.Artifact.RecoveryArtifactId,
                logicalSide = descriptor.LogicalSide,
                provider = descriptor.Provider,
                providerAccountId = descriptor.ProviderAccountId,
                endpointRootBindingId = descriptor.EndpointRootBindingId,
                recoveryRootBindingId = descriptor.RecoveryRootBindingId,
                recoveryCorpusId = descriptor.RecoveryCorpusId,
                retainedFileId = descriptor.RetainedFileId,
                retainedRevision = descriptor.RetainedRevision,
                retainedSize = descriptor.Size,
                retainedNativeAlgorithm = descriptor.NativeAlgorithm,
                destinationPath = target.DestinationPath,
                destinationAbsent = current is null,
                predecessorFileId = current?.Id,
                predecessorRevision = current?.Revision,
                predecessorNativeHash = current?.ContentHash,
                recoveryAllocationId = recoveryAllocation?.AllocationId,
                restoreAsCopy
            });
            await _authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, SyncAuthorityCloudExecution.SourceJson(stage),
                destinationJson, requestEvidence,
                SyncAuthorityCloudExecution.Hash("cloud-recovery-restore-success", fingerprint, destinationJson),
                cancellationToken);
            await _authority.CommitSuccessfulManualRestoreAttemptAsync(attempt.AttemptId, lease, commandId,
                route.Artifact.RecoveryArtifactId, target.DestinationPath, successor.Id!, successor.Revision!,
                successor.Size, successor.ContentHash!, JsonSerializer.Serialize(new
                {
                    result = "manual cloud recovery restore committed over its retained destination preimage",
                    destinationPath = target.DestinationPath,
                    recoveryArtifactId = route.Artifact.RecoveryArtifactId,
                    restoreCommandId = commandId,
                    restoreAsCopy
                }), cancellationToken);
            var committed = await _authority.LoadOperationAsync(operationId, cancellationToken);
            if (committed.Status != SyncAuthorityOperationStatus.Committed)
                throw new SyncAuthorityCloudRecoveryException(
                    "The durable restore operation did not reach its committed boundary; the result is not reported as complete.");
            // The held private stage is closed BEFORE the owned cleanup entry, exactly as the shared cloud
            // update engine closes it before its own committed-boundary finalizer: the cleanup re-opens the
            // exact owned stage file exclusively and must not meet this process's own held handle.
            await stage.DisposeAsync();
            stage = null;
            // This call's own lease is released BEFORE the shared committed-boundary finalizer, exactly as the
            // shared cloud update engine releases its lease before finalization: the finalizer acquires its own
            // fenced lease for the cleanup and completion transitions.
            await leaseRenewal.ReleaseAsync(CancellationToken.None);
            leaseReleased = true;
            var finalized = await SyncAuthorityCloudExecution.FinalizeAsync(_authority, operationId, commandId,
                context, "manual cloud recovery restore", cancellationToken);
            if (finalized.Status != SyncAuthorityOperationStatus.Completed)
                throw new SyncAuthorityCloudRecoveryException(
                    "The committed restore did not reach its owned cleanup boundary; the result is not reported as complete. " + finalized.Reason);
            return new CloudRestoreOutcome(target.DestinationPath,
                "The retained " + port.Definition.DisplayName + " recovery copy of '" +
                route.Descriptor.OriginalPath + "' was restored to '" + target.DestinationPath + "' at the " +
                target.SideLabel + " cloud endpoint" +
                (current is null
                    ? " into an absent destination; no existing object was displaced."
                    : " over a retained predecessor whose exact version was kept in this endpoint's own Recovery copy first."));
        }
        catch (Exception error)
        {
            // CC4E correction (ISSUE-3): a failure BEFORE any attempt was dispatched must not strand a leased
            // operation that blocks the pair's further preparation. The held stage and this call's own lease are
            // closed first, exactly in the order the one shared CC3A cleanup entry needs, and only a proven
            // zero-attempt, unadvanced, exactly-cleaned operation is then abandoned as a no-effect refusal.
            if (stage is not null)
            {
                await stage.DisposeAsync();
                stage = null;
            }
            leaseReleased = true;
            try
            {
                await leaseRenewal.ReleaseAsync(CancellationToken.None);
            }
            catch (Exception releaseError) when (releaseError is InvalidOperationException or InvalidDataException)
            {
                // An already-expired or already-released lease needs no second release; the abandonment below
                // then simply cannot acquire its own fenced lease and the operation stays retained.
            }
            await TryAbandonNoEffectAsync(operationId, commandId, route, target, cloud, controlRoot, error);
            throw;
        }
        finally
        {
            if (stage is not null) await stage.DisposeAsync();
            if (!leaseReleased) await leaseRenewal.ReleaseAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// CC4E correction (ISSUE-3): abandons one manual restore that failed BEFORE any attempt was dispatched, so
    /// a pre-dispatch failure cannot strand a leased operation. The held private stage, its handles and this
    /// call's lease are already closed by the caller; the one shared CC3A cleanup entry then verifies and removes
    /// only this operation's exact private stage and its own then-empty private transaction under a bounded
    /// NONCANCELLED token, and only after that exact outcome is proven does one narrow database helper take the
    /// existing leased-to-superseded terminal transition. An operation with ANY attempt row, an advanced remote
    /// recovery allocation or an unproven cleanup is retained unresolved and nothing is forced or fabricated.
    /// Nothing here reports success and no provider request is issued.
    /// </summary>
    private async Task TryAbandonNoEffectAsync(string operationId, string commandId, CloudRestoreRoute route,
        CloudRestoreTarget target, SyncAuthorityPreparedCloudPair cloud, PilotDirectoryLease controlRoot,
        Exception error)
    {
        // CC4E integration closure (ISSUE-3 bound): the abandonment attempt is itself time-bounded and is
        // deliberately NOT linked to the original cancelled request, so an expired cleanup preserves the
        // ORIGINAL restore failure rather than replacing it, and any unproved cleanup or supersede state
        // stays retained for explicit reconciliation instead of being forced or fabricated.
        var abandonWindow = AbandonCleanupWindowForTest > TimeSpan.Zero
            ? AbandonCleanupWindowForTest
            : TimeSpan.FromSeconds(30);
        using var abandonTimeout = new CancellationTokenSource(abandonWindow);
        var abandonToken = abandonTimeout.Token;
        try
        {
            var operation = await _authority.LoadOperationAsync(operationId, abandonToken);
            if (operation.Attempts.Count != 0) return;
            var cleanup = await SyncAuthorityCloudContentStage.CleanupAsync(_authority, operationId, commandId,
                cloud.PrivateStaging, controlRoot, abandonToken);
            var nothingCreated = !cleanup.StageRemoved && !cleanup.TransactionRemoved;
            if (!cleanup.Cleaned || cleanup.ResidualPaths.Count != 0 || File.Exists(cleanup.StagePath) ||
                Directory.Exists(cleanup.TransactionPath) ||
                !(nothingCreated || cleanup.StageRemoved && cleanup.TransactionRemoved))
                return;
            await using var abandonRenewal = await _authority.BeginFencedOperationLeaseRenewalAsync(operationId, commandId,
                SyncAuthorityLeasePolicy.LeaseDuration, abandonToken);
            var abandonLease = abandonRenewal.Lease;
            abandonToken = abandonRenewal.WorkToken;
            try
            {
                await _authority.SupersedeNoEffectManualRestoreAsync(operationId, abandonLease, commandId,
                    route.Artifact.RecoveryArtifactId, target.DestinationPath, cleanup.Cleaned,
                    cleanup.StageRemoved, cleanup.TransactionRemoved, cleanup.ResidualPaths.Count,
                    JsonSerializer.Serialize(new
                    {
                        result = "manual cloud restore abandoned with no effect; nothing was dispatched",
                        classification = "pre_dispatch_failure_no_effect",
                        adapter = error.GetType().Name,
                        destinationPath = target.DestinationPath,
                        recoveryArtifactId = route.Artifact.RecoveryArtifactId,
                        journalState = cleanup.JournalState,
                        stageRemoved = cleanup.StageRemoved,
                        transactionRemoved = cleanup.TransactionRemoved,
                        residualPaths = cleanup.ResidualPaths.Count
                    }), abandonToken);
            }
            finally
            {
                await abandonRenewal.ReleaseAsync(CancellationToken.None);
            }
        }
        catch (Exception abandonError) when (abandonError is OperationCanceledException or IOException or InvalidDataException or
                                             InvalidOperationException or NotSupportedException or
                                             ArgumentException or UnauthorizedAccessException or JsonException)
        {
            // The original refusal is authoritative and must reach the caller unchanged; an unproven exact
            // cleanup or supersede leaves the operation retained for explicit reconciliation instead.
        }
    }

    /// <summary>
    /// The one retained-plan check of this manual restore: the operation must be the exact fenced Reconcile
    /// operation this call created, with exactly the two retained members and the immutable preconditions this
    /// restore bound. Anything else is refused before a private object or a provider request.
    /// </summary>
    private static void RequireCloudRestorePlan(SyncAuthorityOperationSnapshot operation,
        CloudRestoreTarget target, string commandId)
    {
        if (operation.Kind != SyncAuthorityOperationKind.Reconcile || operation.ObjectId is null ||
            !string.Equals(operation.ObjectId, target.ObjectId, StringComparison.Ordinal) ||
            !string.Equals(operation.PairId, target.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained operation is not this exact manual cloud recovery restore; no request was issued.");
        if (operation.Members.Count != 2)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained restore plan does not carry exactly its affected destination and its retained recovery source member.");
        var affected = operation.Members.SingleOrDefault(member => member.Role == "affected")
            ?? throw new SyncAuthorityCloudRecoveryException(
                "The retained restore plan carries no affected destination member; no request was issued.");
        var recovery = operation.Members.SingleOrDefault(member => member.Role == "recovery")
            ?? throw new SyncAuthorityCloudRecoveryException(
                "The retained restore plan carries no retained recovery source member; no request was issued.");
        if (!string.Equals(affected.RootBindingId, target.Descriptor.EndpointRootBindingId,
                StringComparison.Ordinal) ||
            !string.Equals(affected.ExpectedPathKey, target.DestinationPath, StringComparison.Ordinal) ||
            // The recovery member's own root column stays this pair's endpoint root because a retained recovery
            // root is not a pair root row at all; the exact recovery root binding therefore travels in the
            // member's own immutable expected state and is checked from there.
            !string.Equals(ExpectedStateMember(recovery.ExpectedStateJson, "recoveryRootBindingId"),
                target.Descriptor.RecoveryRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(recovery.RootBindingId, target.Descriptor.EndpointRootBindingId,
                StringComparison.Ordinal) ||
            !string.Equals(recovery.ExpectedPathKey, target.Descriptor.RetainedPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained restore plan does not bind this exact destination path and retained recovery member; no request was issued.");
        SyncAuthorityCloudExecution.RequirePreconditions(operation, target.DestinationPath);
        using var document = JsonDocument.Parse(operation.PreconditionsJson);
        var root = document.RootElement;
        if (!string.Equals(Text(root, "restoreCommandId"), commandId, StringComparison.Ordinal) ||
            !string.Equals(Text(root, "recoveryArtifactId"), target.Artifact.RecoveryArtifactId,
                StringComparison.Ordinal) ||
            !string.Equals(Text(root, "recoveryRootBindingId"), target.Descriptor.RecoveryRootBindingId,
                StringComparison.Ordinal) ||
            !string.Equals(Text(root, "retainedFileId"), target.Descriptor.RetainedFileId,
                StringComparison.Ordinal) ||
            !string.Equals(Text(root, "logicalSide"), target.Descriptor.LogicalSide, StringComparison.Ordinal) ||
            !string.Equals(Text(root, "nativeAlgorithm"), target.Descriptor.NativeAlgorithm,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained restore preconditions do not bind this exact command, recovery artifact, Recovery root and provider-native algorithm; no request was issued.");

        static string? Text(JsonElement element, string member) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(member, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>One string member of a retained member's own expected-state JSON, or null when absent.</summary>
    private static string? ExpectedStateMember(string expectedStateJson, string member)
    {
        try
        {
            using var document = JsonDocument.Parse(expectedStateJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(member, out var value) &&
                   value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the currently read destination is exactly the destination that was verified.</summary>
    private static bool SameCloudRestorePredecessor(PilotRemoteEntry? current, PilotRemoteEntry? verified) =>
        current is null && verified is null ||
        current is not null && verified is not null &&
        string.Equals(current.RelativePath, verified.RelativePath, StringComparison.Ordinal) &&
        string.Equals(current.Id, verified.Id, StringComparison.Ordinal) &&
        string.Equals(current.Revision, verified.Revision, StringComparison.Ordinal) &&
        current.Size == verified.Size &&
        string.Equals(current.ContentHash, verified.ContentHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One read of the exact destination through the target's own typed adapter. It never probes a local path,
    /// never assumes absence from a missing inventory and refuses an ambiguous multi-entry destination.
    /// </summary>
    private static async Task<PilotRemoteEntry?> ReadCloudRestoreDestinationAsync(
        SyncAuthorityCloudEndpointPort port, string destinationPath, CancellationToken cancellationToken)
    {
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await port.Store.ListEntriesAsync(cancellationToken) ??
                      throw new SyncAuthorityCloudRecoveryException(
                          "The target endpoint returned no inventory for its own root; the destination is never assumed absent.");
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        var matches = entries.Where(entry => SyncAuthorityCloudExecution.MatchesPath(entry, destinationPath))
            .ToArray();
        if (matches.Length > 1)
            throw new SyncAuthorityCloudRecoveryException(
                "The target endpoint carries more than one item at the restore destination; nothing was restored.");
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// One bounded, provider-based choice of an absent copy destination inside the target's own retained root.
    /// It uses the existing recovered-copy naming convention and only ever returns a path the target's own
    /// inventory proved absent; it never deletes or overwrites anything to make room for a copy.
    /// </summary>
    private static async Task<string> FindAvailableCloudRecoveryCopyPathAsync(
        SyncAuthorityCloudEndpointPort port, string originalPath, CancellationToken cancellationToken)
    {
        var path = PilotPath.NormalizeRelative(originalPath);
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : path[..slash];
        var name = slash < 0 ? path : path[(slash + 1)..];
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        var prefix = $"{stem} (recovered {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss})";
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await port.Store.ListEntriesAsync(cancellationToken) ??
                      throw new SyncAuthorityCloudRecoveryException(
                          "The target endpoint returned no inventory for its own root; no absent copy path is guessed.");
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        var occupied = entries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var sequence = 0; sequence < 10_000; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = sequence == 0 ? string.Empty : $" {sequence}";
            var candidateName = prefix + suffix + extension;
            var candidate = parent.Length == 0 ? candidateName : parent + "/" + candidateName;
            if (!occupied.Contains(candidate)) return candidate;
        }
        throw new SyncAuthorityCloudRecoveryException(
            "No collision-free recovered-copy name is available in the target endpoint's own root; nothing was copied.");
    }

    private async Task RecordCloudRestoreNotAppliedAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        SyncAuthorityCloudEndpointPort port, SyncAuthorityCloudContentStage stage, string fingerprint,
        string destinationPath, Exception error, CancellationToken cancellationToken)
    {
        await _authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, SyncAuthorityCloudExecution.SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "restore_conditional_write_unsupported",
                adapter = error.GetType().Name,
                provider = port.Provider,
                destinationPath
            }), SyncAuthorityCloudExecution.Hash("cloud-recovery-restore-not-applied", fingerprint),
            cancellationToken);
        await _authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            JsonSerializer.Serialize(new
            {
                reason = "The target adapter conclusively proves it declined the conditional restore before applying it.",
                destinationPath
            }), cancellationToken);
    }

    private async Task RecordCloudRestoreUnknownAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        SyncAuthorityCloudEndpointPort port, SyncAuthorityCloudContentStage stage, string fingerprint,
        string destinationPath, Exception error, CancellationToken cancellationToken)
    {
        await _authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            SyncAuthorityCloudExecution.SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "restore_response_unknown",
                adapter = error.GetType().Name,
                provider = port.Provider,
                destinationPath,
                message = error.Message
            }), SyncAuthorityCloudExecution.Hash("cloud-recovery-restore-unknown", fingerprint),
            cancellationToken);
    }

    /// <summary>
    /// The one independent successor verification: the adapter's own receipt must describe the exact reviewed
    /// size and this target provider's own registered native digest, and the target root must then read back
    /// exactly one file at the destination carrying that same identity, revision, size and digest.
    /// </summary>
    private static async Task<PilotRemoteEntry> VerifyCloudRestoreSuccessorAsync(
        SyncAuthorityCloudEndpointPort port, string destinationPath, SyncAuthorityCloudContentStage stage,
        SyncAuthorityCloudStageDigest digest, PilotUploadResult uploaded, CancellationToken cancellationToken)
    {
        if (uploaded is null || string.IsNullOrWhiteSpace(uploaded.FileId) ||
            string.IsNullOrWhiteSpace(uploaded.Revision) || uploaded.Size != stage.Size ||
            string.IsNullOrWhiteSpace(uploaded.ContentHash) ||
            !string.Equals(uploaded.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRecoveryException(
                "The conditional restore response does not describe the exact reviewed size and target-native digest.");
        var entry = await ReadCloudRestoreDestinationAsync(port, destinationPath, cancellationToken) ??
                    throw new SyncAuthorityCloudRecoveryException(
                        "The target does not carry exactly one restored object at its destination path.");
        if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null ||
            !string.Equals(entry.Id, uploaded.FileId, StringComparison.Ordinal) ||
            !string.Equals(entry.Revision, uploaded.Revision, StringComparison.Ordinal) ||
            entry.Size != stage.Size || entry.ContentHash is null ||
            !string.Equals(entry.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRecoveryException(
                "The independent destination readback differs from the exact restored identity, size or target-native digest.");
        return entry;
    }
}
