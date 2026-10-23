using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

// CC3B-1: the one shared allocation policy.
//
// The exact allocation truth that BuildAllocations produced for every legacy operation kind now lives here
// once, and the reconciliation planner delegates to it. With no cloud configuration the produced list is
// byte-equivalent to the previous implementation - the same IDs, kinds, deterministic names, parents and
// order - because the legacy branch below is that implementation moved verbatim.
//
// The optional cloud configuration does not itself authorize anything: it binds the private staging control
// authority and each logical target's remote recovery authority and maximum simple-upload size. The planner
// only records the resulting allocation rows; the effect-bearing cloud executor rebinds the same
// configuration against its actual typed ports and held private control-root lease before any effect.

/// <summary>
/// One logical cloud target's explicit allocation authority: the retained remote recovery root authority that
/// an update/delete of that target recovers into, and the exact maximum source size that one ordinary
/// simple-upload request may carry before the target requires a reserved upload session. It is deliberately
/// distinct from either logical endpoint root, so a recovery or session allocation can never be mistaken for
/// user content.
/// </summary>
public sealed record SyncAuthorityCloudTargetAllocation(
    string RecoveryRootBindingId,
    long MaximumSimpleUploadBytes,
    long? MaximumSimpleReplacementBytes = null)
{
    public SyncUploadSessionPolicy UploadSessionPolicy => new(MaximumSimpleUploadBytes,
        MaximumSimpleReplacementBytes ?? MaximumSimpleUploadBytes);
}

/// <summary>
/// The one optional final planner configuration for an explicit cloud/cloud pair. It names the private control
/// namespace that owns the transaction and stage allocations, and the two logical targets' recovery and
/// simple-upload authorities. Values are non-secret identifiers and limits only.
/// </summary>
public sealed record SyncAuthorityCloudAllocationConfiguration(
    string StagingControlNamespaceId,
    SyncAuthorityCloudTargetAllocation Left,
    SyncAuthorityCloudTargetAllocation Right)
{
    /// <summary>The exact target authority for one retained compatibility side.</summary>
    public SyncAuthorityCloudTargetAllocation ForSide(string side)
    {
        SyncAuthorityCloudSides.RequireCompatibilityTag(side);
        return string.Equals(side, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal) ? Left : Right;
    }

    /// <summary>
    /// Refuses an incomplete or ambiguous cloud allocation configuration rather than guessing a default. A
    /// missing staging namespace, a missing recovery root or a non-positive limit is refused before any
    /// allocation row is produced.
    ///
    /// Both logical targets may name the SAME recovery root authority. The CC2E recovery scope is one exact
    /// installation, provider, provider account and ACTUAL corpus, so two disjoint folders of one account and
    /// corpus legitimately share that one retained recovery boundary: refusing that composition here would
    /// declare a supported pair unsupported, and widening the recovery scope per logical side, duplicating the
    /// Recovery folder or minting a per-side recovery identity would create a second authority for one corpus.
    /// This configuration is only a planner input and is not authority in either direction. The real
    /// per-target checks remain in SyncAuthorityCloudRecovery.RequireAuthorityAsync, which still requires each
    /// target's own logical side, provider, provider account, namespace, folder, pair-root binding, allocation
    /// parent, installation and held staging namespace, an active retained recovery row carrying exactly that
    /// actual corpus, and a selected pair root that is never its own recovery boundary.
    /// </summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StagingControlNamespaceId);
        foreach (var (label, target) in new[] { ("left", Left), ("right", Right) })
        {
            ArgumentNullException.ThrowIfNull(target, label + " cloud target allocation");
            ArgumentException.ThrowIfNullOrWhiteSpace(target.RecoveryRootBindingId);
            target.UploadSessionPolicy.Validate();
        }
    }
}

/// <summary>
/// The one shared operation-allocation policy used by the reconciliation planner. It records allocation rows
/// only: it performs no provider call, no filesystem effect and no journal transition.
/// </summary>
internal static class SyncAuthorityOperationAllocations
{
    internal static IReadOnlyList<SyncAuthorityAllocation> Build(string operationId,
        SyncAuthorityOperationKind kind, string path, string localRoot, string dropboxRoot,
        string objectKind, long? sourceSize, SyncAuthorityPlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var cloud = options.CloudAllocations;
        if (cloud is null) return BuildLegacy(operationId, kind, path, localRoot, dropboxRoot, objectKind,
            sourceSize, options);
        cloud.Validate();
        return BuildCloud(operationId, kind, path, localRoot, dropboxRoot, objectKind, sourceSize, cloud);
    }

    /// <summary>
    /// The exact legacy policy, unchanged: one private transaction directory and stage file for every local
    /// file create/update, local recovery for a local replacement or deletion, remote recovery for a remote
    /// replacement or deletion, and an upload session only for a large remote create/update.
    /// </summary>
    private static IReadOnlyList<SyncAuthorityAllocation> BuildLegacy(string operationId,
        SyncAuthorityOperationKind kind, string path, string localRoot, string dropboxRoot,
        string objectKind, long? sourceSize, SyncAuthorityPlannerOptions options)
    {
        var result = new List<SyncAuthorityAllocation>();
        if (objectKind != "directory" && kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.DeleteLocal)
        {
            var transaction = Allocation(operationId, "transaction", options.LocalControlNamespaceId,
                SyncAuthorityAllocationKind.TransactionDirectory, options.LocalControlNamespaceId,
                SyncLocalTransactionDirectory.Prefix + Hash(operationId, "transaction")[..32]);
            result.Add(transaction);
            if (kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal)
                result.Add(Allocation(operationId, "stage", options.LocalControlNamespaceId,
                    SyncAuthorityAllocationKind.StageFile, transaction.AllocationId, "stage.partial"));
            if (kind is SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.DeleteLocal)
                result.Add(Allocation(operationId, "recovery", options.LocalControlNamespaceId,
                    SyncAuthorityAllocationKind.LocalRecoveryFile, transaction.AllocationId,
                    kind == SyncAuthorityOperationKind.UpdateLocal
                        ? "recovery-" + Hash(operationId, "recovery")[..32]
                        : "backup.backup"));
        }
        if (objectKind != "directory" &&
            kind is SyncAuthorityOperationKind.UpdateRemote or SyncAuthorityOperationKind.DeleteRemote)
            result.Add(Allocation(operationId, "recovery", options.DropboxControlNamespaceId,
                SyncAuthorityAllocationKind.RemoteRecoveryFile, options.DropboxControlNamespaceId,
                "recovery-" + Hash(operationId, "recovery")[..32]));
        var policy = (options.RemoteUploadSessionPolicy ?? new SyncUploadSessionPolicy(
            SyncUploadLimits.DropboxSingleRequestBytes, SyncUploadLimits.DropboxSingleRequestBytes)).Validate();
        if (objectKind != "directory" && policy.RequiresSession(sourceSize,
                kind == SyncAuthorityOperationKind.UpdateRemote) &&
            kind is SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote)
            result.Add(Allocation(operationId, "upload-session", options.DropboxControlNamespaceId,
                SyncAuthorityAllocationKind.UploadSession, dropboxRoot,
                "upload-session-" + Hash(operationId, path)[..32]));
        return result;
    }

    /// <summary>
    /// The explicit cloud policy. A file create/update on either logical target uses the same private
    /// transaction and stage allocations under the configured private staging control authority; a replacement
    /// or deletion uses the recovering target's own remote recovery authority instead of a local recovery file;
    /// a create/update whose source exceeds the target's configured limit reserves one upload session. A
    /// directory or a move creates no unnecessary file stage. The target is selected only from the retained
    /// operation kind: the Local kinds target the logical left endpoint and the Remote kinds the logical right.
    /// </summary>
    private static IReadOnlyList<SyncAuthorityAllocation> BuildCloud(string operationId,
        SyncAuthorityOperationKind kind, string path, string localRoot, string dropboxRoot,
        string objectKind, long? sourceSize, SyncAuthorityCloudAllocationConfiguration cloud)
    {
        var result = new List<SyncAuthorityAllocation>();
        var targetSide = TargetSide(kind);
        var target = cloud.ForSide(targetSide);
        var targetRoot = string.Equals(targetSide, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal)
            ? localRoot
            : dropboxRoot;
        var file = objectKind != "directory";
        var isCreate = kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote;
        var isUpdate = kind is SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote;
        var isDelete = kind is SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote;
        if (file && (isCreate || isUpdate || isDelete))
        {
            var transaction = Allocation(operationId, "transaction", cloud.StagingControlNamespaceId,
                SyncAuthorityAllocationKind.TransactionDirectory, cloud.StagingControlNamespaceId,
                SyncLocalTransactionDirectory.Prefix + Hash(operationId, "transaction")[..32]);
            result.Add(transaction);
            if (isCreate || isUpdate)
                result.Add(Allocation(operationId, "stage", cloud.StagingControlNamespaceId,
                    SyncAuthorityAllocationKind.StageFile, transaction.AllocationId, "stage.partial"));
            if (isUpdate || isDelete)
                result.Add(Allocation(operationId, "recovery", cloud.StagingControlNamespaceId,
                    SyncAuthorityAllocationKind.RemoteRecoveryFile, target.RecoveryRootBindingId,
                    "recovery-" + Hash(operationId, "recovery")[..32]));
        }
        if (file && target.UploadSessionPolicy.RequiresSession(sourceSize, isUpdate) && (isCreate || isUpdate))
            result.Add(Allocation(operationId, "upload-session", cloud.StagingControlNamespaceId,
                SyncAuthorityAllocationKind.UploadSession, targetRoot,
                "upload-session-" + Hash(operationId, path)[..32]));
        return result;
    }

    /// <summary>
    /// The exact logical target of a cloud operation. Only the two create kinds are effect-enabled; the
    /// remaining kinds resolve their target here so an allocation can never be produced for an unknown side.
    /// </summary>
    internal static string TargetSide(SyncAuthorityOperationKind kind) => kind switch
    {
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.MoveLocal =>
            SyncAuthorityCloudSides.LogicalLeft,
        SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteRemote or SyncAuthorityOperationKind.MoveRemote =>
            SyncAuthorityCloudSides.LogicalRight,
        _ => throw new InvalidDataException(
            "A cloud allocation has no logical target for operation kind " + kind + ".")
    };

    private static SyncAuthorityAllocation Allocation(string operationId, string role, string controlNamespaceId,
        SyncAuthorityAllocationKind kind, string parentIdentity, string? deterministicName = null)
    {
        var suffix = Hash(operationId, role)[..20];
        return new SyncAuthorityAllocation(Id("allocation", operationId, role), controlNamespaceId, kind,
            deterministicName ?? $"{role}-{suffix}", parentIdentity, Hash("owner-nonce", operationId, role));
    }

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
