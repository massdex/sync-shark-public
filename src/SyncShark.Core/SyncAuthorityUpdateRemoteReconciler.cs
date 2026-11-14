using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityUpdateRemoteReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.UpdateRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted remote replacement.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The remote replacement has no reconcilable attempt.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var localVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote replacement source version is absent."), cancellationToken);
        var predecessorVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote replacement predecessor version is absent."), cancellationToken);
        if (localVersion.Side != "local" || localVersion.ContentHash is null || localVersion.Size is null or < 0 ||
            predecessorVersion.Side != "dropbox" || predecessorVersion.ContentHash is null ||
            predecessorVersion.Size is null or < 0)
            throw new InvalidDataException("The retained remote replacement version authority is incomplete.");
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote replacement path is absent."));
        var allocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            // Reconcile an ALREADY ISSUED effect from its retained version and independent remote evidence.
            // A later local edit/deletion cannot undo the issued request or make its outcome unknowable.
            // No source is opened and no transfer is dispatched here. Any subsequent retry still passes
            // the executor's exact-current-source, held-handle and conditional-destination validation.
            SyncDropboxArtifactPaths.RequireRecoveryContainer(recoveryRoot.RootPath);
            await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
            var recovery = await remoteStore.GetRecoveryAsync(recoveryRoot, allocation.DeterministicName,
                cancellationToken);
            return attempt.EffectKind switch
            {
                "dropbox-recovery-copy" => await ReconcileRecoveryAsync(operation, attempt, predecessorVersion,
                    path, allocation, recovery, lease, cancellationToken),
                "dropbox-upload-update" => await ReconcileUpdateAsync(operation, attempt, sourceMember,
                    destinationMember, localVersion, predecessorVersion, path, allocation, recovery,
                    retainedRemoteRoot, lease, cancellationToken),
                _ => throw new InvalidDataException("The interrupted effect is outside remote replacement choreography.")
            };
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileRecoveryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityObjectVersion predecessorVersion, string path, SyncAuthorityAllocation allocation,
        SyncArchiveResult? recovery, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (recovery is null)
        {
            await new SyncAuthorityUpdateRemoteExecutor(pair, remoteStore, authority)
                .LoadExactRemoteAsync(path, predecessorVersion, cancellationToken);
            var proof = "{\"result\":\"remote predecessor recovery copy not applied\"}";
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}", proof,
                SyncAuthorityUpdateRemoteExecutor.Hash("recovery-reconciled-not-applied", proof),
                cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Ready,
                false, "The recovery copy is absent and the predecessor remains exact; one fenced retry is allowed.");
        }
        if (recovery.Size != predecessorVersion.Size ||
            !string.Equals(recovery.ContentHash, predecessorVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            allocation.Status == SyncAuthorityAllocationStatus.Verified &&
            !string.Equals(recovery.FileId, allocation.BoundIdentity, StringComparison.Ordinal))
            return await BlockAsync(operation.OperationId, attempt, lease,
                "{\"result\":\"remote predecessor recovery object is not exact\"}", cancellationToken);

        var recoveryJson = SyncAuthorityUpdateRemoteExecutor.RecoveryJson(recovery);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess, "{}", recoveryJson, recoveryJson,
            SyncAuthorityUpdateRemoteExecutor.Hash("recovery-reconciled-success", recoveryJson),
            cancellationToken);
        if (allocation.Status != SyncAuthorityAllocationStatus.Verified)
            await new SyncAuthorityUpdateRemoteExecutor(pair, remoteStore, authority)
                .AdvanceAllocationAsync(allocation.AllocationId, lease, recovery.FileId, recovery.ContentHash,
                    recovery.Size, cancellationToken);
        await authority.RegisterRecoveryArtifactAsync(
            SyncAuthorityUpdateRemoteExecutor.Id("recovery-artifact", operation.OperationId),
            operation.OperationId, operation.ObjectId!, allocation.AllocationId, "dropbox", recoveryJson,
            recovery.ContentHash, recovery.Size, cancellationToken);
        await authority.CommitReconciledIntermediateAttemptAsync(attempt.AttemptId, lease,
            "{\"result\":\"interrupted remote replacement recovery copy verified\"}", cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Ready,
            false, "The exact predecessor recovery was verified without replay; replacement may continue.");
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileUpdateAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion localVersion,
        SyncAuthorityObjectVersion predecessorVersion, string path, SyncAuthorityAllocation allocation,
        SyncArchiveResult? recovery,
        SyncAuthorityRetainedRootAuthority retainedRemoteRoot,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        RequireRecovery(allocation, recovery, predecessorVersion);
        using var predecessorIdentity = JsonDocument.Parse(predecessorVersion.IdentityJson);
        var predecessorId = predecessorIdentity.RootElement.GetProperty("fileId").GetString()!;
        var predecessorRevision = predecessorIdentity.RootElement.GetProperty("revision").GetString()!;
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var current = await remoteStore.FindEntryAtPathAsync(path, cancellationToken);
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var matches = current is null ? Array.Empty<PilotRemoteEntry>() : [current];
        if (matches.Length == 1 && matches[0].Kind == PilotEntryKind.File &&
            string.Equals(matches[0].RelativePath, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(matches[0].Id, predecessorId, StringComparison.Ordinal) &&
            string.Equals(matches[0].Revision, predecessorRevision, StringComparison.Ordinal) &&
            matches[0].Size == predecessorVersion.Size && string.Equals(matches[0].ContentHash,
                predecessorVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            var proof = "{\"result\":\"conditional remote replacement not applied\"}";
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied,
                "{}", // No fresh local observation: source authority remains the immutable operation member.
                SyncAuthorityUpdateRemoteExecutor.RemoteJson(matches[0]), proof,
                SyncAuthorityUpdateRemoteExecutor.Hash("update-reconciled-not-applied", proof),
                cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Ready,
                false, "The exact predecessor remains; one fenced conditional retry is allowed.");
        }
        if (matches.Length != 1 || matches[0].Kind != PilotEntryKind.File || matches[0].Id is null ||
            matches[0].Revision is null || matches[0].ContentHash is null ||
            !string.Equals(matches[0].RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(matches[0].Id, predecessorId, StringComparison.Ordinal) ||
            string.Equals(matches[0].Revision, predecessorRevision, StringComparison.Ordinal) ||
            matches[0].Size != localVersion.Size || !string.Equals(matches[0].ContentHash,
                localVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(operation.OperationId, attempt, lease,
                "{\"result\":\"remote replacement destination is absent or differs from predecessor and successor authority\"}",
                cancellationToken);

        var successor = matches[0];
        var uploadAllocation = operation.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.UploadSession);
        if (uploadAllocation is not null &&
            !SyncUploadLimits.ForStore(remoteStore).RequiresSession(localVersion.Size, replacement: true))
            return await BlockAsync(operation.OperationId, attempt, lease,
                "{\"result\":\"retained non-session replacement has a legacy session allocation; exact successor and predecessor recovery preserved; explicit compatibility verification required\"}",
                cancellationToken,
                "The expected replacement bytes are present and the predecessor recovery is retained, but this old operation reserved a session that the guarded transfer never used. It remains unresolved for explicit verification; no write was replayed.");
        if (uploadAllocation is not null)
            await new SyncAuthorityUploadSessionCoordinator(authority, uploadAllocation, localVersion, lease)
                .CompleteFromReconciliationAsync(cancellationToken);
        var successorJson = SyncAuthorityUpdateRemoteExecutor.RemoteJson(successor);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess,
            "{}", successorJson, // The issued source version is retained; current local bytes are not asserted.
            "{\"reconciliation\":\"expected conditional successor present\"}",
            SyncAuthorityUpdateRemoteExecutor.Hash("update-reconciled-success", successorJson),
            cancellationToken);
        var remoteIdentity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot,
            successor.Id!, successor.Revision);
        var remoteVersionId = SyncAuthorityUpdateRemoteExecutor.Id("version-dropbox", operation.ObjectId!,
            remoteIdentity, successor.ContentHash!, successor.Size.ToString(CultureInfo.InvariantCulture));
        var remoteMembershipId = SyncAuthorityUpdateRemoteExecutor.Id("membership-dropbox-effect",
            operation.OperationId, path, remoteVersionId);
        var remoteGeneration = await authority.LoadMembershipGenerationAsync(remoteMembershipId, cancellationToken) ??
                               await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId, path,
                                   cancellationToken);
        await authority.RegisterObjectVersionAsync(remoteVersionId, operation.ObjectId!, "dropbox",
            remoteIdentity, successor.ContentHash, successor.Size, cancellationToken);
        await authority.RegisterMembershipAsync(remoteMembershipId, operation.ObjectId!,
            destinationMember.RootBindingId, path, remoteGeneration, cancellationToken: cancellationToken);
        var pointId = SyncAuthorityUpdateRemoteExecutor.Id("syncpoint-effect", operation.OperationId,
            localVersion.ObjectVersionId, remoteVersionId);
        await authority.CommitReconciledSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
            operation.ObjectId!, localVersion.ObjectVersionId, remoteVersionId, sourceMember.MembershipId,
            remoteMembershipId, "{\"result\":\"interrupted remote replacement reconciled\"}",
            cancellationToken);
        await authority.CompleteOperationAsync(lease,
            "{\"result\":\"reconciled remote replacement complete; predecessor retained\"}", cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Completed,
            false, "The exact replacement successor was committed from read-only reconciliation without replay.");
    }

    private static void RequireRecovery(SyncAuthorityAllocation allocation, SyncArchiveResult? recovery,
        SyncAuthorityObjectVersion predecessor)
    {
        if (allocation.Status != SyncAuthorityAllocationStatus.Verified || recovery is null ||
            !string.Equals(recovery.FileId, allocation.BoundIdentity, StringComparison.Ordinal) ||
            recovery.Size != predecessor.Size || !string.Equals(recovery.ContentHash, predecessor.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The remote replacement recovery authority is not exact.");
    }

    private async Task<SyncAuthorityExecutionResult> BlockAsync(string operationId, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, string detail, CancellationToken cancellationToken,
        string? message = null)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", detail,
            SyncAuthorityUpdateRemoteExecutor.Hash("update-reconciled-conflict", detail), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            detail, cancellationToken);
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
            message ?? "The interrupted remote replacement is ambiguous; recovery and both observed states were preserved.");
    }
}
