using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityDeleteRemoteReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.DeleteRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted remote deletion.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The remote deletion has no reconcilable attempt.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote deletion source version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote deletion path is absent."));
        var tombstoneId = TombstoneId(operation.PreconditionsJson);
        if (sourceVersion.ContentHash is null)
        {
            if (attempt.EffectKind != "dropbox-delete-directory")
                throw new InvalidDataException("The interrupted effect is not a Dropbox directory deletion.");
            return await ReconcileDirectoryAsync(operation, attempt, sourceVersion, path, tombstoneId,
                workerId, cancellationToken);
        }
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var allocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            SyncDropboxArtifactPaths.RequireRecoveryContainer(recoveryRoot.RootPath);
            await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
            var recovery = await remoteStore.GetRecoveryAsync(recoveryRoot, allocation.DeterministicName,
                cancellationToken);
            if (attempt.EffectKind == "dropbox-recovery-copy-before-delete")
                return await ReconcileRecoveryAsync(operation, attempt, sourceVersion, allocation, recovery, lease,
                    cancellationToken);
            if (attempt.EffectKind != "dropbox-delete-revision")
                throw new InvalidDataException("The interrupted effect is not part of remote deletion choreography.");
            RequireExactRecovery(recovery, allocation, sourceVersion);
            using var identity = JsonDocument.Parse(sourceVersion.IdentityJson);
            var expectedId = identity.RootElement.GetProperty("fileId").GetString()!;
            var expectedRevision = identity.RootElement.GetProperty("revision").GetString()!;
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var entries = await remoteStore.ListEntriesAsync(cancellationToken);
            var currentById = entries.SingleOrDefault(item => string.Equals(item.Id, expectedId,
                StringComparison.Ordinal));
            if (currentById is null)
            {
                var proof = JsonSerializer.Serialize(new
                {
                    result = "expected Dropbox identity absent",
                    expectedId,
                    expectedRevision,
                    recoveryId = recovery!.FileId,
                    tombstoneId
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                {
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess,
                        JsonSerializer.Serialize(new { path, fileId = expectedId, revision = expectedRevision }),
                        "{\"present\":false}", proof, Hash("delete-reconciled-success", proof), cancellationToken);
                    await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                else
                {
                    await authority.CommitReconciledDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"remote deletion reconciled from exact absence\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the exact Dropbox identity absent and retained its predecessor.");
            }

            var exactPredecessor = string.Equals(currentById.RelativePath, path, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(currentById.Revision, expectedRevision, StringComparison.Ordinal) &&
                                   currentById.Size == sourceVersion.Size &&
                                   string.Equals(currentById.ContentHash, sourceVersion.ContentHash,
                                       StringComparison.OrdinalIgnoreCase);
            if (exactPredecessor)
            {
                var proof = JsonSerializer.Serialize(new { result = "conditional deletion not applied", expectedId,
                    expectedRevision });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied, proof, proof, proof,
                        Hash("delete-reconciled-not-applied", proof), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready, false,
                    "Read-only reconciliation proved the conditional deletion was not applied; a fenced retry is now allowed.");
            }

            var changed = JsonSerializer.Serialize(new
            {
                result = "Dropbox identity changed after interrupted deletion",
                expectedId,
                expectedRevision,
                currentById.Revision
            });
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", changed,
                    Hash("delete-reconciled-conflict", changed), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, changed, cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                "The Dropbox source changed; deletion remains blocked and recovery is retained.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityObjectVersion sourceVersion, string path, string tombstoneId, string workerId,
        CancellationToken cancellationToken)
    {
        if (sourceVersion.Side != "dropbox" || sourceVersion.ContentHash is not null ||
            sourceVersion.Size is not 0)
            throw new InvalidDataException("The retained Dropbox directory deletion authority is incomplete.");
        using var identity = JsonDocument.Parse(sourceVersion.IdentityJson);
        var expectedId = identity.RootElement.GetProperty("fileId").GetString()
                         ?? throw new InvalidDataException("The Dropbox directory identity is absent.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var entries = await remoteStore.ListEntriesAsync(cancellationToken);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var byIdentity = entries.Where(item => string.Equals(item.Id, expectedId,
                StringComparison.Ordinal)).ToArray();
            if (byIdentity.Length == 0)
            {
                var proof = JsonSerializer.Serialize(new
                {
                    result = "expected Dropbox folder identity absent",
                    expectedId,
                    path,
                    tombstoneId
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                {
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess,
                        JsonSerializer.Serialize(new { path, folderId = expectedId }),
                        "{\"present\":false}", proof,
                        Hash("directory-delete-reconciled-success", proof), cancellationToken);
                    await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                else
                {
                    await authority.CommitReconciledDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"remote directory deletion reconciled\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the exact Dropbox folder identity absent.");
            }
            if (byIdentity.Length == 1 && byIdentity[0].Kind == PilotEntryKind.Directory &&
                string.Equals(byIdentity[0].RelativePath, path, StringComparison.OrdinalIgnoreCase))
            {
                var proof = JsonSerializer.Serialize(new
                {
                    result = "Dropbox directory deletion not applied",
                    expectedId,
                    path
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied, proof, proof, proof,
                        Hash("directory-delete-reconciled-not-applied", proof), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The Dropbox folder identity remains at its source; one fenced retry is allowed.");
            }
            var changed = JsonSerializer.Serialize(new
            {
                result = "Dropbox folder identity changed after interrupted deletion",
                expectedId,
                path
            });
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", changed,
                    Hash("directory-delete-reconciled-conflict", changed), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, changed, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Blocked, false,
                "The Dropbox folder identity changed; deletion remains blocked.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileRecoveryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityObjectVersion sourceVersion, SyncAuthorityAllocation allocation,
        SyncArchiveResult? recovery, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (recovery is null || recovery.Size != sourceVersion.Size ||
            !string.Equals(recovery.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            var blocked = "{\"result\":\"interrupted recovery copy is absent or not exact; deletion was never started\"}";
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}", blocked,
                    Hash("recovery-reconciled-not-applied", blocked), cancellationToken);
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Blocked, cancellationToken: cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, blocked, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Blocked,
                false, "The recovery copy was not proved exact; remote deletion remains blocked.");
        }
        var recoveryJson = JsonSerializer.Serialize(new
        {
            recovery.FileId,
            recovery.Revision,
            recovery.RecoveryPath,
            recovery.Size,
            recovery.ContentHash
        });
        if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", recoveryJson, recoveryJson,
                Hash("recovery-reconciled-success", recoveryJson), cancellationToken);
        await AdvanceAllocationAsync(allocation.AllocationId, lease, recovery.FileId, recovery.ContentHash,
            recovery.Size, cancellationToken);
        await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operation.OperationId),
            operation.OperationId, operation.ObjectId!, allocation.AllocationId, "dropbox", recoveryJson,
            recovery.ContentHash, recovery.Size, cancellationToken);
        if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
            await authority.CommitReconciledIntermediateAttemptAsync(attempt.AttemptId, lease,
                "{\"result\":\"interrupted remote deletion recovery copy verified\"}", cancellationToken);
        else
            await authority.CompleteIntermediateAttemptAsync(attempt.AttemptId, lease,
                "{\"result\":\"interrupted remote deletion recovery copy verified\"}", cancellationToken);
        var status = attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown
            ? SyncAuthorityOperationStatus.Ready
            : SyncAuthorityOperationStatus.Validating;
        return new SyncAuthorityExecutionResult(operation.OperationId, status,
            false, "The interrupted recovery copy is exact; the conditional deletion can now continue.");
    }

    private static void RequireExactRecovery(SyncArchiveResult? recovery, SyncAuthorityAllocation allocation,
        SyncAuthorityObjectVersion sourceVersion)
    {
        if (allocation.Status != SyncAuthorityAllocationStatus.Verified || recovery is null ||
            !string.Equals(recovery.FileId, allocation.BoundIdentity, StringComparison.Ordinal) ||
            recovery.Size != sourceVersion.Size ||
            !string.Equals(recovery.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The remote predecessor recovery authority is not exact.");
    }

    private async Task AdvanceAllocationAsync(string allocationId, SyncAuthorityLease lease, string identity,
        string contentHash, long size, CancellationToken cancellationToken)
    {
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Created,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Bound,
            boundIdentity: identity, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Verified,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
    }

    private static string TombstoneId(string preconditionsJson)
    {
        using var document = JsonDocument.Parse(preconditionsJson);
        return document.RootElement.TryGetProperty("tombstoneId", out var value) &&
               value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException("The deletion operation has no retained tombstone authority.");
    }

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];
    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
