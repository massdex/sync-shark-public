using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncAuthorityConflictResolutionPlan(
    string ConflictId,
    long ConflictGeneration,
    string ResolutionId,
    string OperationGroupId,
    string OperationId,
    SyncAuthorityConflictResolutionKind Kind,
    string? PreserveOperationId = null,
    string? ConflictCopyPath = null);

public sealed class SyncAuthorityConflictResolutionCoordinator(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    SyncAuthorityPlannerOptions options,
    SyncAuthorityCommandAdmission admission)
{
    /// <summary>
    /// CC3F-1: the optional typed cloud execution context. Null on the legacy path, so the public legacy
    /// constructor, the old operation identities and the NTFS/Dropbox engines are unchanged.
    /// </summary>
    private readonly SyncAuthorityCloudConflictContext? _cloud;

    /// <summary>
    /// CC3F-1: the internal typed cloud-execution overload. It preserves the public legacy constructor and
    /// adds only the one typed cloud context this coordinator's conflict plan and effects are bound to.
    /// </summary>
    internal SyncAuthorityConflictResolutionCoordinator(SyncPair pair, ISyncRemoteStore remoteStore,
        SyncAuthorityDatabase authority, SyncAuthorityPlannerOptions options,
        SyncAuthorityCommandAdmission admission, SyncAuthorityCloudConflictContext cloud)
        : this(pair, remoteStore, authority, options, admission)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (!string.Equals(pair.Id, cloud.PairId, StringComparison.Ordinal))
            throw new InvalidOperationException("The cloud conflict context belongs to another pair.");
        cloud.RequireSideAuthorities();
        _cloud = cloud;
    }

    /// <summary>True when this coordinator plans and executes an explicit cloud/cloud conflict.</summary>
    internal bool IsCloud => _cloud is not null;

    public async Task<SyncAuthorityConflictResolutionPlan> PlanAsync(string conflictId, long expectedGeneration,
        SyncAuthorityConflictResolutionKind resolutionKind, CancellationToken cancellationToken = default)
    {
        var conflict = await authority.LoadConflictAsync(conflictId, cancellationToken);
        if (conflict.PairId != pair.Id || conflict.ConflictGeneration != expectedGeneration ||
            conflict.Status is not ("open" or "resolution_requested" or "resolving"))
            throw new InvalidOperationException("The selected conflict generation is stale or belongs to another pair.");
        if (!string.Equals(conflict.LocalPath, conflict.DropboxPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Conflict resolution does not accept divergent memberships as a replacement.");
        var retainedLocalVersion = await authority.LoadObjectVersionAsync(conflict.LocalObjectVersionId,
            cancellationToken);
        var retainedDropboxVersion = await authority.LoadObjectVersionAsync(conflict.DropboxObjectVersionId,
            cancellationToken);
        if (!string.Equals(retainedLocalVersion.ObjectId, retainedDropboxVersion.ObjectId,
                StringComparison.Ordinal) ||
            !string.Equals(retainedLocalVersion.ObjectId, conflict.ObjectId, StringComparison.Ordinal) ||
            retainedLocalVersion.ContentHash is null || retainedDropboxVersion.ContentHash is null ||
            retainedLocalVersion.Size is null || retainedDropboxVersion.Size is null)
            throw new InvalidOperationException(
                "This file/folder type conflict remains visible, but automatic file replacement is unsafe. Resolve the ancestor type on one side, then complete a fresh inventory before descendant sync can resume.");
        var path = conflict.LocalPath;
        var groupId = Id("operation-group", conflictId, expectedGeneration.ToString(), resolutionKind.ToString());
        var resolutionId = Id("conflict-resolution", conflictId, expectedGeneration.ToString(),
            resolutionKind.ToString());
        await authority.RegisterConflictResolutionAsync(resolutionId, conflictId, expectedGeneration,
            resolutionKind, groupId, cancellationToken);

        string? preserveOperationId = null;
        string? conflictCopyPath = null;
        if (resolutionKind == SyncAuthorityConflictResolutionKind.PreserveBoth)
        {
            var preserveKey = Id("idempotency", Id("operation", resolutionId, "preserve-dropbox-copy"));
            var preserveRegistration = await authority.ReserveActionAsync(preserveKey, admission, cancellationToken);
            preserveOperationId = preserveRegistration.OperationId;
            // CC3F-1: the visible copy name uses the central display name of the ACTUAL logical-right
            // provider for a new cloud plan; the legacy "Dropbox" wording is preserved byte-for-byte when no
            // typed cloud context is carried, so already-created operations keep their exact id/path bytes.
            conflictCopyPath = ConflictCopyName(conflict, resolutionId,
                _cloud is null ? "Dropbox" : _cloud.Right.Definition.DisplayName);
            var copyObjectId = Id("object-conflict-copy", resolutionId, conflict.DropboxObjectVersionId);
            var preservePreconditions = JsonSerializer.Serialize(new
            {
                conflictId,
                expectedGeneration,
                resolutionKind = "PreserveBoth",
                pairId = pair.Id,
                sourcePath = path,
                destinationPath = conflictCopyPath,
                sourceVersion = conflict.DropboxObjectVersionId,
                destinationAbsent = true,
                configurationRevision = options.ConfigurationRevision
            });
            var preserveMembers = new[]
            {
                new SyncAuthorityMember(Id("member", preserveOperationId, "source"), pair.Id, conflict.ObjectId,
                    "source", conflict.DropboxRootBindingId, conflict.DropboxMembershipId,
                    conflict.DropboxObjectVersionId, conflict.DropboxMembershipGeneration, path,
                    JsonSerializer.Serialize(new
                    {
                        present = true,
                        sourceVersion = conflict.DropboxObjectVersionId,
                        path
                    })),
                new SyncAuthorityMember(Id("member", preserveOperationId, "destination"), pair.Id, copyObjectId,
                    "destination", conflict.DropboxRootBindingId, null, null, null, conflictCopyPath,
                    JsonSerializer.Serialize(new { present = false, path = conflictCopyPath }))
            };
            await authority.RegisterObjectAsync(copyObjectId, "file", cancellationToken);
            await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(preserveOperationId,
                preserveKey, SyncAuthorityOperationKind.ResolveConflict, pair.Id,
                copyObjectId, options.ConfigurationRevision, options.CapabilityProfileId, preservePreconditions,
                110, preserveMembers, [], [], groupId), admission, cancellationToken,
                reservation: preserveRegistration.Reservation);
        }

        var keepLocal = resolutionKind is SyncAuthorityConflictResolutionKind.KeepLocal or
            SyncAuthorityConflictResolutionKind.PreserveBoth;
        var kind = keepLocal ? SyncAuthorityOperationKind.UpdateRemote : SyncAuthorityOperationKind.UpdateLocal;
        var idempotencyKey = Id("idempotency", Id("operation", resolutionId, kind.ToString()));
        var registration = await authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
        var operationId = registration.OperationId;
        var sourceVersion = keepLocal ? conflict.LocalObjectVersionId : conflict.DropboxObjectVersionId;
        var destinationVersion = keepLocal ? conflict.DropboxObjectVersionId : conflict.LocalObjectVersionId;
        var sourceRoot = keepLocal ? conflict.LocalRootBindingId : conflict.DropboxRootBindingId;
        var destinationRoot = keepLocal ? conflict.DropboxRootBindingId : conflict.LocalRootBindingId;
        var sourceMembership = keepLocal ? conflict.LocalMembershipId : conflict.DropboxMembershipId;
        var destinationMembership = keepLocal ? conflict.DropboxMembershipId : conflict.LocalMembershipId;
        var sourceGeneration = keepLocal ? conflict.LocalMembershipGeneration : conflict.DropboxMembershipGeneration;
        var destinationGeneration = keepLocal ? conflict.DropboxMembershipGeneration : conflict.LocalMembershipGeneration;
        var preconditions = JsonSerializer.Serialize(new
        {
            conflictId,
            expectedGeneration,
            resolutionKind = resolutionKind.ToString(),
            pairId = pair.Id,
            path,
            sourceVersion,
            destinationVersion,
            configurationRevision = options.ConfigurationRevision
        });
        var members = new[]
        {
            new SyncAuthorityMember(Id("member", operationId, "source"), pair.Id, conflict.ObjectId,
                "source", sourceRoot, sourceMembership, sourceVersion, sourceGeneration, path,
                JsonSerializer.Serialize(new { present = true, sourceVersion, path })),
            new SyncAuthorityMember(Id("member", operationId, "destination"), pair.Id, conflict.ObjectId,
                "destination", destinationRoot, destinationMembership, destinationVersion,
                destinationGeneration, path,
                JsonSerializer.Serialize(new { present = true, destinationVersion, path }))
        };
        // CC3F-1: a typed cloud conflict takes its allocations from the one shared CC3B allocation policy.
        // Its old LocalAllocations/RemoteAllocations are never used when this plan is a cloud plan, because a
        // cloud UpdateLocal would otherwise allocate a local transaction/stage under an absent NTFS root.
        var allocations = _cloud is null
            ? keepLocal ? RemoteAllocations(operationId) : LocalAllocations(operationId)
            : CloudAllocations(operationId, kind, path,
                keepLocal ? retainedLocalVersion.Size : retainedDropboxVersion.Size);
        await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
            idempotencyKey, kind, pair.Id, conflict.ObjectId, options.ConfigurationRevision,
            options.CapabilityProfileId, preconditions, 100, members,
            preserveOperationId is null
                ? []
                : [new SyncAuthorityDependency(preserveOperationId, "must_complete")],
            allocations, groupId),
            admission, cancellationToken, reservation: registration.Reservation);
        return new SyncAuthorityConflictResolutionPlan(conflictId, expectedGeneration, resolutionId, groupId,
            operationId, resolutionKind, preserveOperationId, conflictCopyPath);
    }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(SyncAuthorityConflictResolutionPlan plan,
        string workerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await authority.MarkConflictResolutionExecutingAsync(plan.ConflictId, plan.OperationGroupId,
            cancellationToken);
        if (plan.Kind == SyncAuthorityConflictResolutionKind.PreserveBoth)
        {
            var preserveId = plan.PreserveOperationId
                ?? throw new InvalidDataException("The preserve-both plan has no copy operation.");
            var preserve = await authority.LoadOperationAsync(preserveId, cancellationToken);
            SyncAuthorityExecutionResult preserveResult;
            if (preserve.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased)
                preserveResult = await ExecutePreserveAsync(preserveId, workerId, cancellationToken);
            else if (preserve.Status == SyncAuthorityOperationStatus.EffectUnknown ||
                     _cloud is not null && preserve.Status is SyncAuthorityOperationStatus.EffectIssuing or
                         SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.Reconciling or
                         SyncAuthorityOperationStatus.Verifying or SyncAuthorityOperationStatus.Committed or
                         SyncAuthorityOperationStatus.CleanupPending)
                preserveResult = await ReconcilePreserveAsync(preserveId, workerId, cancellationToken);
            else if (preserve.Status == SyncAuthorityOperationStatus.Completed)
                preserveResult = new SyncAuthorityExecutionResult(preserveId,
                    SyncAuthorityOperationStatus.Completed, false, "The visible conflict copy is already complete.");
            else
                return new SyncAuthorityExecutionResult(preserveId, preserve.Status, false,
                    "The visible conflict-copy prerequisite is not runnable.");
            if (preserveResult.Status != SyncAuthorityOperationStatus.Completed)
                return preserveResult;
        }

        var primary = await authority.LoadOperationAsync(plan.OperationId, cancellationToken);
        // CC3F-1/-2: a typed cloud conflict replaces the losing logical side through the ONE common CC3C
        // cloud update engine (with its shared CC3C recovery), for keep-left, keep-right and preserve-both
        // alike. The legacy NTFS update engines are never selected for a cloud plan.
        if (_cloud is not null)
        {
            var engine = new SyncAuthorityCloudUpdateExecutor(authority, _cloud.Cloud, _cloud.SideBindings);
            var cloudResult = primary.Status switch
            {
                SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased =>
                    await engine.ExecuteAsync(plan.OperationId, workerId, cancellationToken),
                SyncAuthorityOperationStatus.EffectIssuing or SyncAuthorityOperationStatus.EffectUnknown or
                SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.Reconciling or
                SyncAuthorityOperationStatus.Verifying or SyncAuthorityOperationStatus.Committed or
                SyncAuthorityOperationStatus.CleanupPending or SyncAuthorityOperationStatus.Completed =>
                    await engine.ReconcileAsync(plan.OperationId, workerId, cancellationToken),
                _ => new SyncAuthorityExecutionResult(primary.OperationId, primary.Status, false,
                    "The retained cloud conflict replacement is not runnable.")
            };
            if (cloudResult.Status == SyncAuthorityOperationStatus.Completed)
                await authority.CompleteConflictResolutionAsync(plan.ConflictId, plan.OperationGroupId,
                    cancellationToken);
            return cloudResult;
        }
        var result = plan.Kind switch
        {
            SyncAuthorityConflictResolutionKind.KeepLocal =>
                await ExecuteUpdateRemoteAsync(primary, plan.OperationId, workerId, cancellationToken),
            SyncAuthorityConflictResolutionKind.KeepDropbox =>
                await ExecuteUpdateLocalAsync(primary, plan.OperationId, workerId, cancellationToken),
            SyncAuthorityConflictResolutionKind.PreserveBoth =>
                await ExecuteUpdateRemoteAsync(primary, plan.OperationId, workerId, cancellationToken),
            _ => throw new InvalidOperationException("The conflict-resolution effect graph is not supported.")
        };
        if (result.Status == SyncAuthorityOperationStatus.Completed)
            await authority.CompleteConflictResolutionAsync(plan.ConflictId, plan.OperationGroupId,
                cancellationToken);
        return result;
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteUpdateRemoteAsync(
        SyncAuthorityOperationSnapshot operation, string operationId, string workerId,
        CancellationToken cancellationToken) => operation.Status switch
    {
        SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased =>
            await new SyncAuthorityUpdateRemoteExecutor(pair, remoteStore, authority)
                .ExecuteAsync(operationId, workerId, cancellationToken),
        SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.Reconciling or
            SyncAuthorityOperationStatus.Verifying =>
            await new SyncAuthorityUpdateRemoteReconciler(pair, remoteStore, authority)
                .ReconcileAsync(operationId, workerId, cancellationToken),
        SyncAuthorityOperationStatus.Completed =>
            new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The remote replacement is already complete."),
        _ => new SyncAuthorityExecutionResult(operationId, operation.Status, false,
            "The remote replacement is not runnable.")
    };

    private async Task<SyncAuthorityExecutionResult> ExecuteUpdateLocalAsync(
        SyncAuthorityOperationSnapshot operation, string operationId, string workerId,
        CancellationToken cancellationToken) => operation.Status switch
    {
        SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased =>
            await new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority)
                .ExecuteAsync(operationId, workerId, cancellationToken),
        SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectIssuing =>
            await new SyncAuthorityUpdateLocalReconciler(pair, remoteStore, authority)
                .ReconcileAsync(operationId, workerId, cancellationToken),
        SyncAuthorityOperationStatus.Completed =>
            new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The local replacement is already complete."),
        _ => new SyncAuthorityExecutionResult(operationId, operation.Status, false,
            "The local replacement is not runnable.")
    };

    private IReadOnlyList<SyncAuthorityAllocation> RemoteAllocations(string operationId) =>
    [
        new SyncAuthorityAllocation(Id("allocation", operationId, "recovery"),
            options.DropboxControlNamespaceId, SyncAuthorityAllocationKind.RemoteRecoveryFile,
            "recovery-" + Hash(operationId, "recovery")[..32], options.DropboxControlNamespaceId,
            Hash("owner-nonce", operationId, "recovery"))
    ];

    private IReadOnlyList<SyncAuthorityAllocation> LocalAllocations(string operationId)
    {
        var transaction = new SyncAuthorityAllocation(Id("allocation", operationId, "transaction"),
            options.LocalControlNamespaceId, SyncAuthorityAllocationKind.TransactionDirectory,
            SyncLocalTransactionDirectory.Prefix + Hash(operationId, "transaction")[..32],
            options.LocalControlNamespaceId, Hash("owner-nonce", operationId, "transaction"));
        return
        [
            transaction,
            new SyncAuthorityAllocation(Id("allocation", operationId, "stage"),
                options.LocalControlNamespaceId, SyncAuthorityAllocationKind.StageFile, "stage.partial",
                transaction.AllocationId, Hash("owner-nonce", operationId, "stage")),
            new SyncAuthorityAllocation(Id("allocation", operationId, "recovery"),
                options.LocalControlNamespaceId, SyncAuthorityAllocationKind.LocalRecoveryFile,
                "recovery-" + Hash(operationId, "recovery")[..32], transaction.AllocationId,
                Hash("owner-nonce", operationId, "recovery"))
        ];
    }

    /// <summary>
    /// CC3F-1: the shared CC3B allocation policy for a typed cloud conflict plan. The two logical targets'
    /// retained root bindings are the local/dropbox root inputs the shared policy expects, so the produced
    /// rows are exactly the rows the reconciliation planner would produce for the same operation kind.
    /// </summary>
    private IReadOnlyList<SyncAuthorityAllocation> CloudAllocations(string operationId,
        SyncAuthorityOperationKind kind, string path, long? sourceSize)
    {
        if (options.CloudAllocations is null)
            throw new SyncAuthorityCloudConflictCopyException(
                "The typed cloud conflict plan requires the shared cloud allocation configuration; its old local allocations are never used when logical-left is cloud.");
        return SyncAuthorityOperationAllocations.Build(operationId, kind, path, _cloud!.Left.RootBindingId,
            _cloud.Right.RootBindingId, "file", sourceSize, options);
    }

    private Task<SyncAuthorityExecutionResult> ExecutePreserveAsync(string preserveId, string workerId,
        CancellationToken cancellationToken) => _cloud is null
        ? new SyncAuthorityPreserveConflictCopyExecutor(pair, remoteStore, authority)
            .ExecuteAsync(preserveId, workerId, cancellationToken)
        : new SyncAuthorityCloudConflictCopyExecutor(authority, _cloud)
            .ExecuteAsync(preserveId, workerId, cancellationToken);

    private Task<SyncAuthorityExecutionResult> ReconcilePreserveAsync(string preserveId, string workerId,
        CancellationToken cancellationToken) => _cloud is null
        ? new SyncAuthorityPreserveConflictCopyReconciler(pair, remoteStore, authority)
            .ReconcileAsync(preserveId, workerId, cancellationToken)
        : new SyncAuthorityCloudConflictCopyExecutor(authority, _cloud)
            .ReconcileAsync(preserveId, workerId, cancellationToken);

    private static string ConflictCopyName(SyncAuthorityConflictSnapshot conflict, string resolutionId,
        string providerLabel)
    {
        var original = PilotPath.NormalizeRelative(conflict.DropboxPath);
        var separator = original.LastIndexOf('/');
        var parent = separator < 0 ? string.Empty : original[..separator];
        var originalName = separator < 0 ? original : original[(separator + 1)..];
        var extension = Path.GetExtension(originalName);
        if (extension.Length > 32)
            throw new InvalidDataException("The original extension is too long for a deterministic conflict copy.");
        var stem = Path.GetFileNameWithoutExtension(originalName);
        if (string.IsNullOrWhiteSpace(stem)) stem = originalName;
        var timestamp = conflict.CreatedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Hash("conflict-copy", resolutionId, conflict.DropboxObjectVersionId)[..10];
        var marker = $" (Sync Shark conflict - {providerLabel} - {timestamp} - {suffix})";
        const int maximumNameLength = 200;
        var availableStem = maximumNameLength - marker.Length - extension.Length;
        if (availableStem < 1)
            throw new InvalidDataException("The deterministic conflict-copy name cannot fit safely.");
        if (stem.Length > availableStem) stem = stem[..availableStem];
        var generated = PilotPath.NormalizeTopLevelName(stem + marker + extension);
        return parent.Length == 0 ? generated : PilotPath.NormalizeRelative(parent + "/" + generated);
    }

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
