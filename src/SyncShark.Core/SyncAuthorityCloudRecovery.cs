using System.Text.Json;
using System.Text.Json.Nodes;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3C-2: the one shared, bound cloud recovery helper.
//
// A cloud replacement (and a later cloud deletion) must retain the exact target predecessor in a recovery
// root that is bound to the TARGET's own account and corpus before anything replaces it. The authority for
// that root is never a pair provider, a connection id, a display label or a caller's JSON assertion: the
// helper re-establishes the exact retained recovery scope from the existing database rows (the CC2E recovery
// root found by its exact provider + account + namespace + root + ACTUAL corpus, the operation's own recovery
// allocation and the active provider account the read path already validates) and refuses every disagreement.
//
// The retained recovery artifact keeps the existing recovery_artifact table and its historical physical
// 'dropbox' side bucket; the LOGICAL side travels inside the typed, versioned descriptor this file writes
// into identity_json. Legacy rows are never rewritten and a legacy identity without the explicit descriptor
// kind stays a legacy physical identity rather than being relabelled as a cloud one.

/// <summary>One safe refusal of the bound cloud recovery helper. It is never a partial or unverified copy.</summary>
public sealed class SyncAuthorityCloudRecoveryException(string message)
    : SyncAuthorityCloudExecutionException(message);

/// <summary>
/// The typed, versioned identity descriptor of one generic cloud recovery artifact. It carries the ACTUAL
/// storage authority (provider, provider account, recovery root binding, recovery namespace, folder and
/// corpus), the LOGICAL endpoint position the predecessor was replaced at (side and endpoint root binding)
/// and the exact retained object identity of the copy. The logical side is deliberately separate from the
/// historical physical 'dropbox' recovery_artifact.side bucket.
/// </summary>
public sealed record SyncAuthorityCloudRecoveryDescriptor(
    string Provider,
    string ProviderAccountId,
    string LogicalSide,
    string EndpointRootBindingId,
    string RecoveryRootBindingId,
    string RecoveryProviderAccountId,
    string RecoveryNamespaceId,
    string RecoveryFolderId,
    string RecoveryCorpusId,
    string RecoveryPath,
    string NativeAlgorithm,
    string RetainedFileId,
    string? RetainedRevision,
    string RetainedPath,
    string? ContentHash,
    long Size,
    string OriginalPath)
{
    /// <summary>The explicit descriptor kind. An identity without it is a legacy physical identity.</summary>
    public const string IdentityKindValue = "cloud-recovery";

    /// <summary>The descriptor schema version this build writes and is the only version it accepts.</summary>
    public const int IdentityVersionValue = 1;

    /// <summary>
    /// The exact persisted bytes of this descriptor. 'originalPath' stays at the JSON root because the
    /// existing recovery-artifact projection already reads that exact member, so an existing consumer keeps
    /// its meaning while every new member is additive.
    /// </summary>
    public string Serialize() => JsonSerializer.Serialize(new
    {
        identityKind = IdentityKindValue,
        identityVersion = IdentityVersionValue,
        provider = Provider,
        providerAccountId = ProviderAccountId,
        logicalSide = LogicalSide,
        endpointRootBindingId = EndpointRootBindingId,
        recoveryRootBindingId = RecoveryRootBindingId,
        recoveryProviderAccountId = RecoveryProviderAccountId,
        recoveryNamespaceId = RecoveryNamespaceId,
        recoveryFolderId = RecoveryFolderId,
        recoveryCorpusId = RecoveryCorpusId,
        recoveryPath = RecoveryPath,
        nativeAlgorithm = NativeAlgorithm,
        retainedFileId = RetainedFileId,
        retainedRevision = RetainedRevision,
        retainedPath = RetainedPath,
        contentHash = ContentHash,
        sizeBytes = Size,
        originalPath = OriginalPath
    });

    /// <summary>
    /// Reads a retained descriptor. A JSON value with no explicit descriptor kind is the legacy identity and
    /// returns false so the existing storage/consumer meaning is preserved. An explicit descriptor of another
    /// kind or version, a missing required member or a negative size is refused rather than guessed.
    /// </summary>
    public static bool TryParse(string identityJson, out SyncAuthorityCloudRecoveryDescriptor? descriptor)
    {
        descriptor = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(identityJson);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The recovery identity is not valid JSON; it cannot be treated as legacy authority.", error);
        }
        if (node is not JsonObject root)
            throw new InvalidDataException("The recovery identity must be an object.");
        if (!root.ContainsKey("identityKind")) return false;
        if (!TryReadString(root, "identityKind", out var kind) || string.IsNullOrWhiteSpace(kind))
            throw new InvalidDataException("The recovery identity has an invalid explicit kind.");
        if (!string.Equals(kind, IdentityKindValue, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The recovery identity declares an unsupported identity kind; it is refused without an effect.");
        if (!TryReadNumber(root, "identityVersion", out var version) ||
            version != IdentityVersionValue)
            throw new InvalidDataException(
                "The recovery identity declares an unsupported descriptor version; it is refused without an effect.");
        var size = TryReadNumber(root, "sizeBytes", out var retained)
            ? retained
            : throw new InvalidDataException(
                "The recovery descriptor has no exact retained size; it is refused without an effect.");
        if (size < 0)
            throw new InvalidDataException(
                "The recovery descriptor carries a negative retained size; it is refused without an effect.");
        descriptor = new SyncAuthorityCloudRecoveryDescriptor(
            RequireMember(root, "provider"),
            RequireMember(root, "providerAccountId"),
            SyncAuthorityCloudSides.RequireCompatibilityTag(RequireMember(root, "logicalSide")),
            RequireMember(root, "endpointRootBindingId"),
            RequireMember(root, "recoveryRootBindingId"),
            RequireMember(root, "recoveryProviderAccountId"),
            RequireMember(root, "recoveryNamespaceId"),
            RequireMember(root, "recoveryFolderId"),
            RequireMember(root, "recoveryCorpusId"),
            RequireMember(root, "recoveryPath"),
            RequireMember(root, "nativeAlgorithm"),
            RequireMember(root, "retainedFileId"),
            ReadString(root, "retainedRevision"),
            RequireMember(root, "retainedPath"),
            ReadString(root, "contentHash"),
            size,
            RequireMember(root, "originalPath"));
        return true;
    }

    private static string RequireMember(JsonObject root, string member)
    {
        var value = ReadString(root, member);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                "The cloud recovery descriptor is missing its required '" + member +
                "' member; it is refused without an effect.");
        return value!;
    }

    private static string? ReadString(JsonObject root, string member) =>
        TryReadString(root, member, out var value) ? value : null;

    private static bool TryReadString(JsonObject root, string member, out string? value)
    {
        value = null;
        if (!root.TryGetPropertyValue(member, out var node) || node is not JsonValue jsonValue) return false;
        if (!jsonValue.TryGetValue<string>(out var text)) return false;
        value = text;
        return true;
    }

    private static bool TryReadNumber(JsonObject root, string member, out long value)
    {
        value = 0;
        if (!root.TryGetPropertyValue(member, out var node) || node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<long>(out var number))
        {
            value = number;
            return true;
        }
        if (jsonValue.TryGetValue<int>(out var small))
        {
            value = small;
            return true;
        }
        return false;
    }
}

/// <summary>
/// The validated recovery authority of one logical target: the exact provider and provider account of the
/// target port, that target's own endpoint root binding, and the ACTUAL retained recovery root (binding,
/// account, namespace, folder, corpus and path) this target's predecessor must be retained in. Every member
/// comes from the retained database rows, never from a caller's assertion alone.
/// </summary>
internal sealed record SyncAuthorityCloudRecoveryAuthority(
    string LogicalSide,
    string Provider,
    string ProviderAccountId,
    string EndpointRootBindingId,
    string RecoveryRootBindingId,
    string RecoveryProviderAccountId,
    string RecoveryNamespaceId,
    string RecoveryFolderId,
    string RecoveryCorpusId,
    string RecoveryPath)
{
    /// <summary>The exact adapter binding of this retained recovery root.</summary>
    internal SyncRemoteRecoveryRootBinding Binding =>
        new(RecoveryPath, RecoveryFolderId, RecoveryNamespaceId);
}

/// <summary>
/// The one outcome of the shared recovery-before-effect step: either the exact verified descriptor, or the
/// safe refusal the caller must return unchanged. A refusal never leaves the target mutated.
/// </summary>
internal sealed record SyncAuthorityCloudRecoveryOutcome(
    SyncAuthorityCloudRecoveryDescriptor? Descriptor,
    SyncAuthorityExecutionResult? Refusal,
    bool Dispatched);

/// <summary>
/// The one shared bound cloud recovery helper, usable by the update engine and by a later delete engine.
/// </summary>
internal static class SyncAuthorityCloudRecovery
{
    /// <summary>The exact effect kind of the recovery-copy request this helper dispatches.</summary>
    internal const string RecoveryEffectKind = "cloud-recovery-copy";

    /// <summary>
    /// Re-establishes and validates one logical target's exact CC2E recovery authority against the retained
    /// database rows. The supplied side authority must be the target port's own account and pair root, and the
    /// recovery scope it names must be exactly a retained ACTIVE recovery root of that same provider account
    /// with the same namespace, folder and ACTUAL corpus. The operation's own recovery allocation must belong
    /// to that exact binding and to the held private staging authority.
    /// </summary>
    internal static async Task<SyncAuthorityCloudRecoveryAuthority> RequireAuthorityAsync(
        SyncAuthorityDatabase authority, SyncAuthorityCloudEndpointPort targetPort,
        SyncAuthorityCloudSideAuthority? side, SyncAuthorityAllocation allocation, string stagingNamespaceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(allocation);
        if (side is null)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained cloud recovery authority of this logical target was not supplied. A pair provider, a connection id or a selected account is never a recovery authority.");
        var sideTag = SyncAuthorityCloudSides.RequireCompatibilityTag(side.CompatibilitySide);
        if (!string.Equals(sideTag, targetPort.CompatibilitySide, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The supplied side recovery authority belongs to another logical side than the exact target port.");
        foreach (var (label, value) in new[]
                 {
                     ("provider", side.Provider), ("provider account", side.ProviderAccountId),
                     ("namespace", side.NamespaceId), ("folder", side.FolderId),
                     ("pair root binding", side.PairRootBindingId),
                     ("recovery root binding", side.RecoveryRootBindingId),
                     ("recovery namespace", side.RecoveryNamespaceId),
                     ("recovery folder", side.RecoveryFolderId), ("recovery corpus", side.RecoveryCorpusId),
                     ("recovery path", side.RecoveryPath)
                 })
            if (string.IsNullOrWhiteSpace(value))
                throw new SyncAuthorityCloudRecoveryException(
                    "The supplied side recovery authority has no exact " + label + ".");
        if (!string.Equals(side.Provider, targetPort.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(side.ProviderAccountId, targetPort.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(side.NamespaceId, targetPort.NamespaceId, StringComparison.Ordinal) ||
            !string.Equals(side.FolderId, targetPort.FolderId, StringComparison.Ordinal) ||
            !string.Equals(side.PairRootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The supplied side recovery authority is not this exact logical port's own provider account and pair root.");
        if (string.Equals(side.RecoveryRootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The recovery authority names the pair root itself; a pair root is never its own recovery boundary.");
        if (string.Equals(side.RecoveryNamespaceId, side.NamespaceId, StringComparison.Ordinal) &&
            string.Equals(side.RecoveryFolderId, side.FolderId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The recovery authority names the pair root's own folder; a recovery copy is never taken over the replaced object.");
        if (allocation.Kind != SyncAuthorityAllocationKind.RemoteRecoveryFile)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained allocation of a cloud recovery is not a remote recovery allocation.");
        if (!string.Equals(allocation.IntendedParentIdentity, side.RecoveryRootBindingId,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery allocation belongs to another recovery authority than this target's own retained recovery root.");
        if (!string.Equals(allocation.ControlNamespaceId,
                SyncControlNamespace.NormalizeNamespaceId(stagingNamespaceId), StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery allocation does not belong to this exact held private staging authority.");
        var installationId = await authority.LoadInstallationIdAsync(cancellationToken) ??
                             throw new SyncAuthorityCloudRecoveryException(
                                 "The retained installation identity is unavailable; no recovery authority can be bound.");
        var retained = await authority.FindCloudRecoveryRootAsync(installationId, targetPort.Provider,
            targetPort.ProviderAccountId, side.RecoveryNamespaceId, side.RecoveryFolderId,
            side.RecoveryCorpusId, cancellationToken) ??
            throw new SyncAuthorityCloudRecoveryException(
                "No retained active recovery root carries exactly this provider, provider account, namespace, folder and ACTUAL corpus. Supplied values are never the authority for a recovery root.");
        if (!string.Equals(retained.RecoveryRootBindingId, side.RecoveryRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(retained.Provider, targetPort.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(retained.ProviderAccountId, targetPort.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(retained.NamespaceId, side.RecoveryNamespaceId, StringComparison.Ordinal) ||
            !string.Equals(retained.FolderId, side.RecoveryFolderId, StringComparison.Ordinal) ||
            !string.Equals(retained.ProviderCorpusId, side.RecoveryCorpusId, StringComparison.Ordinal) ||
            !string.Equals(retained.Status, "active", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(retained.DisplayPath) ||
            !string.Equals(retained.DisplayPath, side.RecoveryPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery root row disagrees with the supplied recovery authority; the retained row is the authority and the disagreement is refused.");
        return new SyncAuthorityCloudRecoveryAuthority(sideTag, targetPort.Provider,
            targetPort.ProviderAccountId, targetPort.RootBindingId, retained.RecoveryRootBindingId,
            retained.ProviderAccountId!, retained.NamespaceId!, retained.FolderId, retained.ProviderCorpusId!,
            retained.DisplayPath);
    }

    /// <summary>
    /// Retains the exact target predecessor under the validated recovery authority before anything replaces
    /// it, or returns the exact refusal the caller must return unchanged. A verified recovery is never copied
    /// again, an unresolved recovery attempt is never copied again, and every copy must pass an independent
    /// bound-root readback of its exact file identity, size and native content hash.
    /// </summary>
    internal static async Task<SyncAuthorityCloudRecoveryOutcome> EnsureRetainedAsync(
        SyncAuthorityDatabase authority, SyncAuthorityCloudEndpointPort targetPort,
        SyncAuthorityCloudRecoveryAuthority recovery, SyncAuthorityAllocation allocation,
        PilotRemoteEntry predecessor, SyncAuthorityOperationSnapshot operation, string originalPath,
        string nativeAlgorithm, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(lease);
        if (allocation.Status == SyncAuthorityAllocationStatus.Verified)
        {
            // A verified recovery is the retained authority of this predecessor. It is re-read independently
            // and never copied a second time, so a restart cannot duplicate the retained predecessor.
            SyncArchiveResult retained;
            try
            {
                retained = await RequireRetainedAsync(targetPort, recovery, allocation, predecessor,
                    cancellationToken);
            }
            catch (Exception error) when (IsRecoveryFailure(error))
            {
                return Refuse(operation, SyncAuthorityOperationStatus.Blocked, false,
                    "The previously verified recovery object is no longer exact: " + error.Message +
                    " No target update was admitted and the retained allocation and evidence are preserved.");
            }
            var verified = Describe(recovery, predecessor, retained, originalPath, nativeAlgorithm);
            await CompleteRecoveryBookkeepingAsync(authority, operation, allocation, verified, lease,
                cancellationToken);
            return new SyncAuthorityCloudRecoveryOutcome(verified, null, false);
        }
        var unresolved = operation.Attempts.SingleOrDefault(item =>
            string.Equals(item.EffectKind, RecoveryEffectKind, StringComparison.Ordinal) &&
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived
                or SyncAuthorityAttemptStatus.EffectUnknown);
        if (unresolved is not null)
            // A recovery request that was sent and not terminally resolved is never repeated. Blindly copying
            // again could retain a second object for the same predecessor or hide an applied and lost copy.
            return Refuse(operation, SyncAuthorityOperationStatus.EffectUnknown, true,
                "A recovery-copy request for this predecessor was already dispatched and is not terminally resolved; no second recovery copy was issued and no target update was admitted.");
        await targetPort.Store.VerifyRecoveryRootAsync(recovery.Binding, cancellationToken);
        if (allocation.Status == SyncAuthorityAllocationStatus.Authorized)
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
        else if (allocation.Status != SyncAuthorityAllocationStatus.Creating)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery allocation is not at a resumable pre-copy boundary; its status is " +
                allocation.Status + ".");
        var fingerprint = Hash("cloud-recovery", operation.OperationId, predecessor.Id!, predecessor.Revision!,
            recovery.RecoveryRootBindingId, recovery.RecoveryCorpusId, allocation.DeterministicName);
        var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
            RecoveryEffectKind, cancellationToken);
        SyncArchiveResult copied;
        try
        {
            copied = await targetPort.Store.CopyToRecoveryAsync(predecessor, recovery.Binding,
                allocation.DeterministicName, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await RecordUnknownAsync(authority, attempt, lease, predecessor, recovery, fingerprint, error,
                cancellationToken);
            return Refuse(operation, SyncAuthorityOperationStatus.EffectUnknown, true,
                "The recovery-copy response is unknown and no target update was admitted; the predecessor is untouched and the recovery allocation and evidence are preserved.");
        }
        var copiedJson = RemoteJson(copied);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownSuccess,
            RemoteJson(predecessor), copiedJson, copiedJson,
            Hash("cloud-recovery-success", fingerprint, copiedJson), cancellationToken);
        SyncArchiveResult retainedAfterCopy;
        try
        {
            retainedAfterCopy = await RequireRetainedAsync(targetPort, recovery, allocation, predecessor,
                cancellationToken, copied);
        }
        catch (Exception error) when (IsRecoveryFailure(error))
        {
            await BlockAsync(authority, attempt, lease,
                "{\"result\":\"recovery readback drift\"}", cancellationToken);
            return Refuse(operation, SyncAuthorityOperationStatus.Blocked, true,
                "The recovery copy did not read back as the exact predecessor: " + error.Message +
                " No target update was admitted and the retained allocation and evidence are preserved.");
        }
        await AdvanceAllocationAsync(authority, allocation.AllocationId, lease, retainedAfterCopy,
            cancellationToken);
        var descriptor = Describe(recovery, predecessor, retainedAfterCopy, originalPath, nativeAlgorithm);
        await RegisterArtifactAsync(authority, operation, allocation, descriptor, cancellationToken);
        await CompleteIntermediateAsync(authority, attempt, lease, cancellationToken);
        return new SyncAuthorityCloudRecoveryOutcome(descriptor, null, true);
    }

    /// <summary>
    /// The independent bound-root readback of one retained recovery object: the bound recovery root is
    /// re-verified, the adapter's own recovery lookup is re-read and its exact file identity, retained size
    /// and registered native content hash must equal the retained predecessor, with the retained object name
    /// still the exact owned deterministic name inside that bound root.
    /// </summary>
    /// <summary>
    /// CC4E-2: the one shared manual-restore PREIMAGE step. A manual restore that conditionally replaces an
    /// existing destination must retain that exact current target version first, in the SAME actual endpoint
    /// Recovery root its own retained recovery authority names, through the existing two steps of this helper:
    /// the target's own recovery authority is re-established from the retained rows, and the exact predecessor
    /// - file identity, revision, size and provider-native digest - is retained and durably registered as one
    /// typed recovery artifact BEFORE anything replaces it. The restore's own destination path is recorded as
    /// the retained artifact's original path, so the preimage stays correlated with the exact restore it
    /// protects. A refusal here is returned unchanged and no target request is ever issued after it.
    /// </summary>
    internal static async Task<SyncAuthorityCloudRecoveryOutcome> EnsureRestorePreimageAsync(
        SyncAuthorityDatabase authority, SyncAuthorityCloudEndpointPort targetPort,
        SyncAuthorityCloudSideAuthority targetSide, SyncAuthorityAllocation recoveryAllocation,
        PilotRemoteEntry predecessor, SyncAuthorityOperationSnapshot operation, string destinationPath,
        string nativeAlgorithm, string stagingNamespaceId, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetSide);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var recovery = await RequireAuthorityAsync(authority, targetPort, targetSide, recoveryAllocation,
            stagingNamespaceId, cancellationToken);
        if (!string.Equals(recovery.RecoveryRootBindingId, targetSide.RecoveryRootBindingId,
                StringComparison.Ordinal) ||
            !string.Equals(recovery.RecoveryPath, targetSide.RecoveryPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery authority of this manual restore is not this logical target's own current Recovery root; the destination preimage was not retained and nothing was replaced.");
        return await EnsureRetainedAsync(authority, targetPort, recovery, recoveryAllocation, predecessor,
            operation, destinationPath, nativeAlgorithm, lease, cancellationToken);
    }

    private static async Task<SyncArchiveResult> RequireRetainedAsync(SyncAuthorityCloudEndpointPort targetPort,
        SyncAuthorityCloudRecoveryAuthority recovery, SyncAuthorityAllocation allocation,
        PilotRemoteEntry predecessor, CancellationToken cancellationToken, SyncArchiveResult? copied = null)
    {
        if (string.IsNullOrWhiteSpace(predecessor.ContentHash) || predecessor.Size < 0)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained predecessor has no exact native content hash and size to verify a recovery copy against.");
        var binding = recovery.Binding;
        await targetPort.Store.VerifyRecoveryRootAsync(binding, cancellationToken);
        var retained = await targetPort.Store.GetRecoveryAsync(binding, allocation.DeterministicName,
            cancellationToken);
        if (retained is null)
            throw new SyncAuthorityCloudRecoveryException(
                "The bound recovery root retains no object under this operation's exact deterministic recovery name.");
        if (string.IsNullOrWhiteSpace(retained.FileId) || string.IsNullOrWhiteSpace(retained.Revision) ||
            string.IsNullOrWhiteSpace(retained.ContentHash) || string.IsNullOrWhiteSpace(retained.RecoveryPath))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery object has no exact file identity, revision, native hash or recovery path.");
        if (!string.Equals(Path.GetFileName(retained.RecoveryPath), allocation.DeterministicName,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery object does not carry this operation's exact deterministic recovery name inside the bound recovery root.");
        if (allocation.BoundIdentity is not null &&
            !string.Equals(retained.FileId, allocation.BoundIdentity, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery object is not the exact object this operation's allocation is bound to.");
        if (copied is not null &&
            (!string.Equals(copied.FileId, retained.FileId, StringComparison.Ordinal) ||
             !string.Equals(copied.Revision, retained.Revision, StringComparison.Ordinal)))
            throw new SyncAuthorityCloudRecoveryException(
                "The independent recovery readback differs from the exact retained copy response identity.");
        if (retained.Size != predecessor.Size ||
            !string.Equals(retained.ContentHash, predecessor.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRecoveryException(
                "The retained recovery object does not carry the exact predecessor size and native content hash.");
        await targetPort.Store.VerifyRecoveryRootAsync(binding, cancellationToken);
        return retained;
    }

    private static SyncAuthorityCloudRecoveryDescriptor Describe(SyncAuthorityCloudRecoveryAuthority recovery,
        PilotRemoteEntry predecessor, SyncArchiveResult retained, string originalPath, string nativeAlgorithm) =>
        new(recovery.Provider, recovery.ProviderAccountId, recovery.LogicalSide, recovery.EndpointRootBindingId,
            recovery.RecoveryRootBindingId, recovery.RecoveryProviderAccountId, recovery.RecoveryNamespaceId,
            recovery.RecoveryFolderId, recovery.RecoveryCorpusId, recovery.RecoveryPath, nativeAlgorithm,
            retained.FileId, retained.Revision, retained.RecoveryPath, retained.ContentHash, retained.Size,
            originalPath);

    private static async Task CompleteRecoveryBookkeepingAsync(SyncAuthorityDatabase authority,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAllocation allocation,
        SyncAuthorityCloudRecoveryDescriptor descriptor, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        await RegisterArtifactAsync(authority, operation, allocation, descriptor, cancellationToken);
        if (operation.Status != SyncAuthorityOperationStatus.EffectObserved) return;
        var attempt = operation.Attempts.SingleOrDefault(item =>
            string.Equals(item.EffectKind, RecoveryEffectKind, StringComparison.Ordinal) &&
            item.Status == SyncAuthorityAttemptStatus.ResponseReceived);
        if (attempt is null) return;
        await CompleteIntermediateAsync(authority, attempt, lease, cancellationToken);
    }

    private static async Task RegisterArtifactAsync(SyncAuthorityDatabase authority,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAllocation allocation,
        SyncAuthorityCloudRecoveryDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (operation.ObjectId is null)
            throw new SyncAuthorityCloudRecoveryException(
                "The retained operation has no exact logical object identity; no recovery artifact is registered.");
        // The existing recovery_artifact side bucket stays the historical PHYSICAL cloud bucket; the logical
        // endpoint side travels inside the typed descriptor. Legacy rows and the SQL side constraint are
        // untouched, and an exact replay of the same owned descriptor identity is idempotent.
        await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operation.OperationId),
            operation.OperationId, operation.ObjectId, allocation.AllocationId, "dropbox",
            descriptor.Serialize(), descriptor.ContentHash!, descriptor.Size, cancellationToken);
    }

    private static async Task AdvanceAllocationAsync(SyncAuthorityDatabase authority, string allocationId,
        SyncAuthorityLease lease, SyncArchiveResult retained, CancellationToken cancellationToken)
    {
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Created,
            boundIdentity: retained.FileId, contentHash: retained.ContentHash, size: retained.Size,
            cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Bound,
            boundIdentity: retained.FileId, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Verified,
            boundIdentity: retained.FileId, contentHash: retained.ContentHash, size: retained.Size,
            cancellationToken: cancellationToken);
    }

    private static async Task CompleteIntermediateAsync(SyncAuthorityDatabase authority,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, CancellationToken cancellationToken) =>
        await authority.CompleteIntermediateAttemptAsync(attempt.AttemptId, lease,
            "{\"result\":\"target predecessor retained in its bound recovery root\"}", cancellationToken);

    private static async Task RecordUnknownAsync(SyncAuthorityDatabase authority, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, PilotRemoteEntry predecessor, SyncAuthorityCloudRecoveryAuthority recovery,
        string fingerprint, Exception error, CancellationToken cancellationToken) =>
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            RemoteJson(predecessor), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "recovery_response_unknown",
                adapter = error.GetType().Name,
                provider = recovery.Provider,
                recoveryRootBindingId = recovery.RecoveryRootBindingId,
                recoveryCorpusId = recovery.RecoveryCorpusId
            }), Hash("cloud-recovery-unknown", fingerprint), cancellationToken);

    private static async Task BlockAsync(SyncAuthorityDatabase authority, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, string detailJson, CancellationToken cancellationToken)
    {
        try
        {
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, detailJson, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            // The operation's own retained state is authoritative; a refusal to reconcile here never masks
            // the exact refusal returned to the caller.
        }
    }

    private static SyncAuthorityCloudRecoveryOutcome Refuse(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityOperationStatus status, bool dispatched, string reason) =>
        new(null, new SyncAuthorityExecutionResult(operation.OperationId, status, dispatched, reason), dispatched);

    private static bool IsRecoveryFailure(Exception error) =>
        error is IOException or InvalidDataException or NotSupportedException or InvalidOperationException
            or ArgumentException;

    private static string RemoteJson(PilotRemoteEntry entry) => JsonSerializer.Serialize(new
    {
        entry.RelativePath, entry.Id, entry.Revision, entry.Size, entry.ContentHash
    });

    private static string RemoteJson(SyncArchiveResult result) => JsonSerializer.Serialize(new
    {
        result.FileId, result.Revision, result.RecoveryPath, result.Size, result.ContentHash
    });
}
