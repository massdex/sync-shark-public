using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityDeleteRemoteExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.DeleteRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
                SyncAuthorityOperationStatus.Validating))
            throw new InvalidOperationException("The requested authority operation is not a runnable remote deletion for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote deletion source version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote deletion path is absent."));
        var tombstoneId = TombstoneId(operation.PreconditionsJson);
        if (sourceVersion.ContentHash is null)
            return await ExecuteDirectoryAsync(operation, sourceVersion, path, tombstoneId, workerId,
                cancellationToken);
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            var remote = await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
            SyncArchiveResult recovery;
            if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified)
            {
                recovery = await RequireVerifiedRecoveryAsync(recoveryRoot, recoveryAllocation, remote,
                    cancellationToken);
            }
            else
            {
                SyncDropboxArtifactPaths.RequireRecoveryContainer(recoveryRoot.RootPath);
                await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
                await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
                var recoveryFingerprint = Hash("remote-delete-recovery", pair.Id, remote.Id!, remote.Revision!,
                    recoveryRoot.RootFolderId, recoveryAllocation.DeterministicName);
                var recoveryAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, recoveryFingerprint,
                    "dropbox-recovery-copy-before-delete", cancellationToken);
                dispatched = true;
                try
                {
                    recovery = await remoteStore.CopyToRecoveryAsync(remote, recoveryRoot,
                        recoveryAllocation.DeterministicName, cancellationToken);
                }
                catch (DropboxEffectUncertainException)
                {
                    await authority.RecordReceiptAsync(recoveryAttempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.Unknown, RemoteJson(remote), "{}",
                        "{\"classification\":\"delete recovery response unknown\"}",
                        Hash("recovery-unknown", recoveryFingerprint), cancellationToken);
                    return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                        true, "The recovery-copy response is unknown; remote deletion was not started.");
                }
                var recoveryJson = JsonSerializer.Serialize(new
                {
                    recovery.FileId,
                    recovery.Revision,
                    recovery.RecoveryPath,
                    recovery.Size,
                    recovery.ContentHash
                });
                await authority.RecordReceiptAsync(recoveryAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), recoveryJson, recoveryJson,
                    Hash("recovery-success", recoveryFingerprint, recoveryJson), cancellationToken);
                await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease, recovery.FileId,
                    recovery.ContentHash, recovery.Size, cancellationToken);
                await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                    operation.ObjectId!, recoveryAllocation.AllocationId, "dropbox", recoveryJson,
                    recovery.ContentHash, recovery.Size, cancellationToken);
                await authority.CompleteIntermediateAttemptAsync(recoveryAttempt.AttemptId, lease,
                    "{\"result\":\"remote predecessor retained before deletion\"}", cancellationToken);
            }

            SyncDropboxArtifactPaths.RequireRecoveryContainer(recoveryRoot.RootPath);
            await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
            var retained = await remoteStore.GetRecoveryAsync(recoveryRoot,
                recoveryAllocation.DeterministicName, cancellationToken);
            if (retained is null || !string.Equals(retained.FileId, recovery.FileId, StringComparison.Ordinal) ||
                !string.Equals(retained.Revision, recovery.Revision, StringComparison.Ordinal) ||
                retained.Size != remote.Size ||
                !string.Equals(retained.ContentHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained Dropbox deletion recovery object is not exact.");
            remote = await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
            var deleteFingerprint = Hash("delete-remote", pair.Id, remote.Id!, remote.Revision!, path,
                sourceVersion.ObjectVersionId, recovery.FileId, recovery.Revision, tombstoneId);
            var deleteAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, deleteFingerprint,
                "dropbox-delete-revision", cancellationToken);
            try
            {
                await remoteStore.DeleteAsync(remote, cancellationToken);
                PhaseForTest?.Invoke("remote-deleted-before-receipt");
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(deleteAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, RemoteJson(remote), "{}",
                    "{\"classification\":\"conditional deletion response unknown\"}",
                    Hash("delete-unknown", deleteFingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true, "The predecessor is retained, but the conditional deletion response is unknown.");
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(deleteAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(remote), "{}",
                    JsonSerializer.Serialize(new { classification = "revision_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("delete-not-applied", deleteFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(deleteAttempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"conditional deletion rejected; source retained or changed\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked,
                    true, "Dropbox rejected the revision-conditioned deletion; recovery remains retained." + " " + SyncActivityText.Failure(exception));
            }
            await RequireDeletedIdentityAbsentAsync(remote.Id!,
                "Dropbox returned success but the deleted file identity remains visible.", cancellationToken);
            var resultJson = JsonSerializer.Serialize(new
            {
                deletedFileId = remote.Id,
                deletedRevision = remote.Revision,
                recovery.FileId,
                recovery.Revision,
                tombstoneId
            });
            await authority.RecordReceiptAsync(deleteAttempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), "{\"present\":false}",
                resultJson, Hash("delete-success", deleteFingerprint, resultJson), cancellationToken);
            await authority.CommitSuccessfulDeletionAttemptAsync(deleteAttempt.AttemptId, lease, tombstoneId,
                pair.Id, operation.ObjectId!, "{\"result\":\"remote deletion verified; predecessor retained\"}",
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"remote deletion complete; predecessor retained\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox revision was conditionally deleted after its predecessor was retained.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion sourceVersion, string path,
        string tombstoneId, string workerId, CancellationToken cancellationToken)
    {
        if (sourceVersion.Side != "dropbox" || sourceVersion.ContentHash is not null ||
            sourceVersion.Size is not 0)
            throw new InvalidDataException("The retained Dropbox directory deletion authority is incomplete.");
        if (!remoteStore.SupportsConditionalDirectoryDelete)
            // Preserve the actual durable status. The runner maps an incomplete result
            // to Blocked; inventing a status transition here would make its drain loop
            // mistake every refusal for progress and repeatedly redispatch this item.
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "This provider cannot remove a folder atomically only when it is empty. " +
                "The folder remains unchanged; no deletion or recovery effect was attempted.")
                { NotAttempted = true };
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            var remote = await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
            var descendants = (await remoteStore.ListEntriesAsync(cancellationToken)).Any(item =>
                item.RelativePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
            if (descendants)
                throw new IOException(
                    "The Dropbox folder deletion waits until every included descendant is recoverably retired.");
            var fingerprint = Hash("delete-remote-directory", pair.Id, remote.Id!, path,
                sourceVersion.ObjectVersionId, tombstoneId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                fingerprint, "dropbox-delete-directory", cancellationToken);
            dispatched = true;
            var recoveryArtifactId = Id("recovery-artifact", operation.OperationId);
            var recoveryJson = JsonSerializer.Serialize(new
            {
                path,
                originalPath = path,
                kind = "directory",
                recipe = "recreate_empty_directory",
                deletedIdentity = remote.Id,
                provenance = "pre_delete_directory_recipe"
            });
            await authority.RegisterRecoveryArtifactAsync(recoveryArtifactId, operation.OperationId,
                operation.ObjectId!, null, "dropbox", recoveryJson,
                Hash("directory-recovery", operation.OperationId, path, remote.Id!), 0,
                cancellationToken);
            try
            {
                await remoteStore.DeleteAsync(remote, cancellationToken);
                PhaseForTest?.Invoke("remote-directory-deleted-before-receipt");
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, RemoteJson(remote), "{}",
                    "{\"classification\":\"directory deletion response unknown\"}",
                    Hash("delete-remote-directory-unknown", fingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The empty folder deletion response is unknown and will be reconciled by stable identity.");
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(remote), "{}",
                    JsonSerializer.Serialize(new { classification = "identity_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("delete-remote-directory-not-applied", fingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"directory deletion rejected; source retained or changed\"}",
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Blocked, true,
                    "Dropbox rejected the stable-identity directory deletion." + " " + SyncActivityText.Failure(exception));
            }
            await RequireDeletedIdentityAbsentAsync(remote.Id!,
                "Dropbox returned success but the deleted folder identity remains visible.", cancellationToken);
            var resultJson = JsonSerializer.Serialize(new
            {
                deletedFolderId = remote.Id,
                path,
                tombstoneId,
                recoveryArtifactId
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), "{\"present\":false}",
                resultJson, Hash("delete-remote-directory-success", fingerprint, resultJson),
                cancellationToken);
            await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                pair.Id, operation.ObjectId!,
                "{\"result\":\"empty remote directory membership removed\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"remote directory deletion complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact empty Dropbox folder identity was deleted after a durable recreate recipe was retained.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncArchiveResult> RequireVerifiedRecoveryAsync(
        SyncRemoteRecoveryRootBinding recoveryRoot, SyncAuthorityAllocation allocation, PilotRemoteEntry remote,
        CancellationToken cancellationToken)
    {
        SyncDropboxArtifactPaths.RequireRecoveryContainer(recoveryRoot.RootPath);
        await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        var retained = await remoteStore.GetRecoveryAsync(recoveryRoot, allocation.DeterministicName,
            cancellationToken);
        if (retained is null || !string.Equals(retained.FileId, allocation.BoundIdentity, StringComparison.Ordinal) ||
            retained.Size != remote.Size || !string.Equals(retained.ContentHash, remote.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The previously retained Dropbox deletion recovery object is not exact.");
        return retained;
    }

    // SS-D10: the post-delete proof asks one bounded question - does this exact deleted provider identity
    // still exist? A native adapter that exposes an exact object-ID lookup answers it directly, so the
    // deletion no longer spends a whole root listing on it. Every other adapter keeps its existing
    // conservative inventory fallback, which is the only absence evidence it can offer. Either way a failed
    // or unavailable observation leaves the deletion unproven instead of implying absence, and the stronger
    // absence consumers (directory pre-delete emptiness, cross-pair subtree proof) are unchanged.
    private async Task RequireDeletedIdentityAbsentAsync(string deletedObjectId, string reason,
        CancellationToken cancellationToken)
    {
        if (remoteStore is ISyncRemoteObjectIdentityLookup lookup)
        {
            if (await lookup.ObjectExistsAsync(deletedObjectId, cancellationToken))
                throw new DropboxEffectUncertainException(reason);
            return;
        }
        var current = await remoteStore.ListEntriesAsync(cancellationToken);
        if (current.Any(item => string.Equals(item.Id, deletedObjectId, StringComparison.Ordinal)))
            throw new DropboxEffectUncertainException(reason);
    }

    private async Task<PilotRemoteEntry> LoadExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        if (expected.Side != "dropbox")
            throw new InvalidDataException("The retained Dropbox deletion authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedId = identity.RootElement.GetProperty("fileId").GetString();
        var expectedRevision = identity.RootElement.GetProperty("revision").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        // One fresh identity-conditioned read at this exact path; the provider is never
        // asked to enumerate the whole destination tree to prove the deletion predecessor.
        var current = await remoteStore.ReadEntryAsync(path, expectedId
                ?? throw new InvalidDataException("The retained Dropbox deletion object ID is absent."),
            cancellationToken);
        var exact = string.Equals(current.Id, expectedId, StringComparison.Ordinal) &&
                    string.Equals(current.RelativePath, path, StringComparison.OrdinalIgnoreCase) &&
                    (expected.ContentHash is null
                        ? current.Kind == PilotEntryKind.Directory && expected.Size is 0
                        : current.Kind == PilotEntryKind.File && expected.Size is not null &&
                          string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal) &&
                          current.Size == expected.Size && string.Equals(current.ContentHash,
                              expected.ContentHash, StringComparison.OrdinalIgnoreCase));
        if (!exact)
            throw new IOException("The current Dropbox deletion source differs from the immutable operation member.");
        return current;
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

    private static string RemoteJson(PilotRemoteEntry remote) => JsonSerializer.Serialize(new
    {
        remote.RelativePath, remote.Id, remote.Revision, remote.Size, remote.ContentHash
    });

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
