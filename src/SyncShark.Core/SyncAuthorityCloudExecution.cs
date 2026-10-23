using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// CC3C-1: the small shared cloud execution surface.
//
// This file holds ONLY infrastructure that both cloud effect engines must share and that already existed
// inside the accepted CC3B create executor: the held private staging context, the one exact relative-path
// matcher, the one adapter exception classification, the identifier/hash primitive and the one
// committed-boundary finalizer with its exact owned CC3A cleanup. It is an extraction, not a redesign: the
// create executor's decisions, refusal messages, journal transitions, receipts and effects are unchanged,
// and no second engine, journal allocation scheme or provider branch is introduced here.

/// <summary>
/// One safe refusal of the shared cloud execution surface: the held private staging authority, the operation
/// shape and the committed cleanup boundary are shared by every cloud effect engine, so their refusals are
/// one type. It is never a partial effect.
/// </summary>
public class SyncAuthorityCloudExecutionException(string message) : IOException(message);

/// <summary>
/// The one internal cloud execution context: the CC2C typed two-port set of exactly one explicit
/// cloud/cloud pair, the existing private control-namespace binding, its actually held private control-root
/// lease and the selected-anchor identity that lease was opened within. It carries no local pair path, so a
/// cloud effect can never be bound to an NTFS selected root.
/// </summary>
internal sealed record SyncAuthorityCloudExecutionContext(
    SyncAuthorityCloudEndpointSet Endpoints,
    SyncControlNamespaceBinding ControlNamespace,
    PilotDirectoryLease ControlRoot)
{
    internal string PairId => Endpoints.PairId;

    internal SyncAuthorityCloudEndpointPort Port(string side) =>
        string.Equals(SyncAuthorityCloudSides.RequireCompatibilityTag(side), SyncAuthorityCloudSides.LogicalLeft,
            StringComparison.Ordinal)
            ? Endpoints.Left
            : Endpoints.Right;

    internal static string OtherSide(string side) =>
        string.Equals(SyncAuthorityCloudSides.RequireCompatibilityTag(side), SyncAuthorityCloudSides.LogicalLeft,
            StringComparison.Ordinal)
            ? SyncAuthorityCloudSides.LogicalRight
            : SyncAuthorityCloudSides.LogicalLeft;

    /// <summary>
    /// Re-establishes that the held root really is this context's exact private control namespace: the
    /// existing binding validates, the held lease still binds its own identity, and the held directory is
    /// the deterministic private control root of the bound namespace. A selected pair root is never accepted.
    /// </summary>
    internal void RequireHeld()
    {
        SyncControlNamespace.Validate(ControlNamespace);
        ControlRoot.VerifyBinding();
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(ControlNamespace.NamespaceId);
        if (!string.Equals(Path.GetFileName(ControlRoot.Path),
                SyncControlNamespace.LocalPrefix + namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The held lease is not the exact private control namespace this cloud operation's allocations belong to.");
    }
}

/// <summary>
/// The one small shared static surface of both cloud effect engines: one exact relative-path matcher, one
/// adapter exception classification, the identifier/hash primitive and the one committed-boundary finalizer.
/// </summary>
internal static class SyncAuthorityCloudExecution
{
    internal static bool MatchesPath(PilotRemoteEntry entry, string path)
    {
        try
        {
            return string.Equals(PilotPath.NormalizeRelative(entry.RelativePath), path, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The one shared exception classification. Only an adapter contract that itself declares the operation
    /// unsupported proves non-application; every other status, transport or provider exception after the
    /// request boundary is an unknown outcome and is never guessed as applied or not applied.
    /// </summary>
    internal static bool ConclusiveNonApplication(Exception error) => error is NotSupportedException;

    internal static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    internal static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();

    /// <summary>
    /// The one exact retained relative path of a member: it must already be its normalized form, so a
    /// dot-segment, separator or escape variation is refused rather than silently normalized into a path the
    /// retained plan never named.
    /// </summary>
    internal static string RequireExactRetainedPath(SyncAuthorityMember member)
    {
        var retained = member.ExpectedPathKey ??
                       throw new SyncAuthorityCloudExecutionException(
                           "The member has no exact retained relative path.");
        string normalized;
        try
        {
            normalized = PilotPath.NormalizeRelative(retained);
        }
        catch (ArgumentException)
        {
            throw new SyncAuthorityCloudExecutionException(
                "The retained path is not a safe exact relative path.");
        }
        if (!string.Equals(normalized, retained, StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The retained path is not already its exact normalized form.");
        return normalized;
    }

    /// <summary>
    /// The one retained-precondition check: the operation's own preconditions JSON must not name another
    /// pair, another operation kind, another path or another configuration revision than the retained
    /// operation and its exact member path.
    /// </summary>
    internal static void RequirePreconditions(SyncAuthorityOperationSnapshot operation, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(operation.PreconditionsJson);
        }
        catch (JsonException)
        {
            throw new SyncAuthorityCloudExecutionException(
                "The retained operation preconditions are not readable JSON.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SyncAuthorityCloudExecutionException(
                    "The retained operation preconditions are not an object.");
            if (root.TryGetProperty("pairId", out var pairId) && pairId.ValueKind == JsonValueKind.String &&
                !string.Equals(pairId.GetString(), operation.PairId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudExecutionException(
                    "The retained preconditions name another pair; the plan is refused.");
            if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                !string.Equals(kind.GetString(), SyncAuthorityNames.OperationKind(operation.Kind),
                    StringComparison.Ordinal))
                throw new SyncAuthorityCloudExecutionException(
                    "The retained preconditions name another operation kind; the plan is refused.");
            if (root.TryGetProperty("path", out var retained) && retained.ValueKind == JsonValueKind.String &&
                !string.Equals(retained.GetString(), path, StringComparison.Ordinal))
                throw new SyncAuthorityCloudExecutionException(
                    "The retained preconditions name another path than the exact retained member path.");
            if (root.TryGetProperty("configurationRevision", out var revision) &&
                revision.ValueKind == JsonValueKind.Number && revision.GetInt64() != operation.ConfigurationRevision)
                throw new SyncAuthorityCloudExecutionException(
                    "The retained preconditions name another configuration revision; the plan is refused.");
        }
    }

    /// <summary>
    /// The one typed cloud version identity check, used for the source of a create/update and for the retained
    /// predecessor a replacement must match. It requires the version to belong to the exact member, to carry
    /// its side, to hold an accepted explicit cloud observation identity of the port's provider, provider
    /// account and native namespace, and to hold a digest produced with that provider's registered content
    /// algorithm.
    /// </summary>
    internal static SyncAuthorityCloudObservationIdentity RequireRetainedVersionIdentity(
        SyncAuthorityObjectVersion version, SyncAuthorityMember member, SyncAuthorityCloudEndpointPort port,
        string role)
    {
        if (!string.Equals(version.ObjectId, member.ObjectId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The retained object version does not belong to the exact " + role + " member.");
        if (!string.Equals(version.Side, port.CompatibilitySide, StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The retained " + role + " version belongs to another logical side than its typed cloud port.");
        SyncAuthorityCloudObservationIdentity? identity;
        try
        {
            if (!SyncAuthorityCloudObservationIdentity.TryParse(version.IdentityJson, out identity) ||
                identity is null)
                throw new SyncAuthorityCloudExecutionException(
                    "The retained " + role +
                    " version carries no accepted typed cloud observation identity; a legacy or malformed identity is refused rather than relabelled.");
        }
        catch (InvalidDataException error)
        {
            throw new SyncAuthorityCloudExecutionException(
                "The retained typed cloud observation identity is refused without an effect: " + error.Message);
        }
        if (!string.Equals(identity.Provider, port.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ProviderAccountId, port.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(identity.NamespaceId, port.NamespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The retained " + role +
                " identity names another provider, provider account or native namespace than the typed " + role +
                " port.");
        if (string.IsNullOrWhiteSpace(identity.FileId))
            throw new SyncAuthorityCloudExecutionException(
                "The retained " + role + " identity has no stable provider file identity.");
        if (!string.Equals(identity.ContentAlgorithm, port.Definition.ContentHashAlgorithm,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The retained " + role +
                " identity was not produced with its port provider's registered content algorithm.");
        var directory = string.Equals(version.ContentHash, null, StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(version.ContentHash);
        if (directory)
        {
            if (version.Size is not (null or 0))
                throw new SyncAuthorityCloudExecutionException(
                    "The retained directory " + role +
                    " carries a content size; it is refused rather than reinterpreted.");
        }
        else
        {
            if (version.Size is null || version.Size.Value < 0 ||
                !SyncContentIdentity.SupportsIncrementalHashing(identity.ContentAlgorithm) ||
                !SyncContentIdentity.IdentityMatchesAlgorithm(identity.ContentAlgorithm, version.ContentHash))
                throw new SyncAuthorityCloudExecutionException(
                    "The retained " + role +
                    " digest does not match its registered content algorithm; it is refused rather than relabelled.");
        }
        return identity;
    }

    /// <summary>The retained object kind of a member's own expected state, preserving its exact meaning.</summary>
    internal static string ObjectKind(SyncAuthorityMember member)
    {
        using var state = JsonDocument.Parse(member.ExpectedStateJson);
        return state.RootElement.TryGetProperty("ObjectKind", out var pascal)
            ? pascal.GetString() ?? "file"
            : state.RootElement.TryGetProperty("objectKind", out var camel)
                ? camel.GetString() ?? "file"
                : "file";
    }

    /// <summary>The exact persisted current-state JSON of one verified successor entry.</summary>
    internal static object SuccessorState(PilotRemoteEntry entry) => new
    {
        entry.RelativePath,
        entry.Kind,
        entry.Id,
        entry.Revision,
        entry.Size,
        entry.ContentHash
    };

    /// <summary>The exact evidence JSON of one verified private stage's retained source object.</summary>
    internal static string SourceJson(SyncAuthorityCloudContentStage stage) => JsonSerializer.Serialize(new
    {
        stage.Source.Provider,
        stage.Source.ProviderAccountId,
        stage.Source.NamespaceId,
        stage.Source.RootBindingId,
        stage.Source.ObjectId,
        stage.Source.ObjectVersionId,
        stage.Source.RelativePath,
        stage.Source.FileId,
        stage.Source.Revision,
        stage.Source.NativeAlgorithm,
        nativeHash = stage.NativeHash,
        commonSha256 = stage.CommonSha256,
        stage.Size
    });

    /// <summary>
    /// The one independent root-bound readback of an exact file successor: the typed target root is verified,
    /// the inventory is read, the root is verified again and exactly one item at the exact path must carry the
    /// held stage's size and its target-native digest under the target provider's own registered algorithm.
    /// </summary>
    internal static async Task<PilotRemoteEntry?> ReadBackExactFileAsync(SyncAuthorityCloudEndpointPort port,
        string path, SyncAuthorityCloudContentStage stage, CancellationToken cancellationToken)
    {
        var digest = await stage.ComputeNativeDigestAsync(port.Provider, cancellationToken);
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        var entry = await port.Store.FindEntryAtPathAsync(path, cancellationToken);
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        if (entry is null) return null;
        return entry.Kind == PilotEntryKind.File && entry.TransferBlockedReason is null &&
               !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Revision) &&
               entry.Size == stage.Size && entry.ContentHash is not null &&
               string.Equals(entry.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase)
            ? entry
            : null;
    }

    /// <summary>
    /// The one unresolved-effect disposition: the operation is left explicitly unresolved with its private
    /// stage retained and zero target mutation is issued. An unsuccessful verification supplies neither
    /// known success nor verified non-application: preserve the original unresolved operation/attempt and
    /// receipt instead of terminally reclassifying uncertainty as an ordinary blocked failure.
    /// </summary>
    internal static Task<SyncAuthorityExecutionResult> UnresolvedAsync(SyncAuthorityDatabase authority,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        string reason, string noun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ReconcileAttemptAsync accepts terminal dispositions. Calling it with Blocked here erased the
        // effect_unknown state even though the exact unknown receipt and active session still existed.
        // The held caller's fenced lease is released by its existing finally; no attempt is completed,
        // made retryable, superseded or dispatched merely because readback could not establish authority.
        return Task.FromResult(new SyncAuthorityExecutionResult(operation.OperationId, operation.Status,
            false, reason + " The private stage is retained and no target request was issued."));
    }

    /// <summary>
    /// The one retained upload-session binding: the reserved session must belong to this exact private
    /// staging authority and to the exact target root, and the typed target adapter must really implement the
    /// resumable session. The existing coordinator binds the checkpoint to the exact original source version,
    /// native digest and size. A session that the retained plan never reserved returns null.
    /// </summary>
    internal static async Task<(SyncAuthorityUploadSessionCoordinator Coordinator,
        SyncUploadSessionCheckpoint? Checkpoint)?> PrepareUploadSessionAsync(SyncAuthorityDatabase authority,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityCloudEndpointPort targetPort,
        SyncAuthorityObjectVersion sourceVersion, SyncAuthorityCloudExecutionContext context,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var allocation = operation.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.UploadSession);
        var replacement = operation.Kind is SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.UpdateRemote;
        var policy = SyncUploadLimits.ForStore(targetPort.Store);
        if (allocation is null)
        {
            if (policy.RequiresSession(sourceVersion.Size, replacement))
                throw new SyncAuthorityCloudExecutionException(
                    "This destination requires a resumable session that the retained plan did not reserve. Review a fresh plan; no upload was issued.");
            return null;
        }
        if (replacement && policy.MaximumSimpleReplacementBytes == long.MaxValue)
            throw new SyncAuthorityCloudExecutionException(
                "The retained replacement reserves a session that the destination's guarded transfer does not use. Verify the retained operation before reviewing a fresh plan; no replacement was issued.");
        if (!string.Equals(allocation.ControlNamespaceId,
                SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId),
                StringComparison.Ordinal) ||
            !string.Equals(allocation.IntendedParentIdentity, targetPort.RootBindingId,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudExecutionException(
                "The reserved upload session does not belong to this private staging authority and target root.");
        if (targetPort.Store is not ISyncResumableRemoteStore)
            throw new SyncAuthorityCloudExecutionException(
                "The retained plan requires a resumable upload session, but the typed target adapter has no resumable implementation.");
        var coordinator = new SyncAuthorityUploadSessionCoordinator(authority, allocation, sourceVersion, lease);
        var checkpoint = await coordinator.PrepareAsync(cancellationToken);
        return (coordinator, checkpoint);
    }

    /// <summary>
    /// The one committed-boundary finalizer shared by both cloud engines. A completed operation is a no-op;
    /// only the exact committed or cleanup-pending boundary is accepted; the one CC3A cleanup entry removes
    /// only this operation's own stage and its own then-empty private transaction; the operation then
    /// completes. It never recopies, never reissues an effect and never removes a foreign artifact.
    /// </summary>
    internal static async Task<SyncAuthorityExecutionResult> FinalizeAsync(SyncAuthorityDatabase authority,
        string operationId, string workerId, SyncAuthorityCloudExecutionContext context, string noun,
        CancellationToken cancellationToken)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The " + noun + " is already complete; finalization is a no-op.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending))
            throw new SyncAuthorityCloudExecutionException(
                "The " + noun + " has no committed cleanup boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var hasPrivateStage = operation.Allocations.Any(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        if (operation.Status == SyncAuthorityOperationStatus.Committed)
        {
            var startLeaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
                SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
            var startLease = startLeaseRenewal.Lease;
            try
            {
                await authority.MarkCleanupPendingAsync(startLease,
                    "{\"result\":\"" + noun + " cleanup started\"}", cancellationToken);
            }
            finally
            {
                await startLeaseRenewal.ReleaseAsync();
            }
        }
        if (hasPrivateStage)
            // The one CC3A cleanup entry re-establishes the exact owned allocation identities, deletes only
            // this operation's own stage and then-empty private transaction, and releases or durably blocks
            // every owned allocation. It never recopies and never removes a foreign artifact.
            await SyncAuthorityCloudContentStage.CleanupAsync(authority, operationId, workerId,
                context.ControlNamespace, context.ControlRoot, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"" + noun + " and its owned cleanup verified\"}", cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
            "The committed " + noun + " and its owned private cleanup were verified without replay.");
    }
}
