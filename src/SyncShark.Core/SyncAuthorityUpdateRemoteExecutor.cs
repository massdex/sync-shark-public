using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityUpdateRemoteExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    /// <summary>
    /// The bounded in-process phase seam the focused proofs use, mirroring the local-replacement executor. It
    /// has no production default, performs no provider, filesystem or journal effect itself and never changes
    /// a decision.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.UpdateRemote || operation.PairId != pair.Id ||
            // H7/G-2: an interrupted upload leaves the operation in the legitimate retained 'Validating'
            // recovery state that this engine itself produces after its verified predecessor retention, so
            // this exact engine must accept it under the same lease, fence, source-revision and
            // recovery-allocation checks rather than declaring the retained work unreachable. The state is a
            // resumable pre-effect boundary, not permission to repeat an uncertain provider effect.
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
                SyncAuthorityOperationStatus.Validating))
            throw new InvalidOperationException("The requested authority operation is not a runnable remote replacement for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        var localVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote replacement source version is absent."), cancellationToken);
        var remotePredecessor = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote replacement predecessor version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote replacement path is absent."));
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var localScan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                refreshPaths: [path], traverseDirectory: _ => false, contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!localScan.Entries.TryGetValue(path, out var local))
                throw new IOException("The exact local replacement source is no longer present.");
            RequireExactLocalSource(local, localVersion);
            await using var localSource = await PilotLocalScanner.OpenValidatedForUploadAsync(local, root,
                cancellationToken, contentProvider: pair.Provider, expectedPairId: pair.Id);
            var remote = await LoadExactRemoteAsync(path, remotePredecessor, cancellationToken);
            SyncArchiveResult recovery;
            if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified)
            {
                recovery = await RequireVerifiedRecoveryAsync(recoveryRoot, recoveryAllocation, remote,
                    cancellationToken);
            }
            else
            {
                SyncDropboxArtifactPaths.RequireRecoveryContainerIfDropbox(pair, recoveryRoot.RootPath);
                await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
                if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Authorized)
                    await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                        SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
                else if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Creating)
                    throw new InvalidOperationException(
                        "The remote predecessor recovery allocation is not resumable.");
                var recoveryFingerprint = Hash("remote-recovery", pair.Id, remote.Id!, remote.Revision!,
                    recoveryRoot.RootFolderId, recoveryAllocation.DeterministicName);
                var recoveryAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, recoveryFingerprint,
                    "dropbox-recovery-copy", cancellationToken);
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
                        "{\"classification\":\"recovery copy response unknown\"}",
                        Hash("recovery-unknown", recoveryFingerprint), cancellationToken);
                    return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                        true, "Recovery-copy response is unknown; the replacement upload was not started.");
                }
                var recoveryJson = RecoveryJson(recovery);
                await authority.RecordReceiptAsync(recoveryAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), recoveryJson, recoveryJson,
                    Hash("recovery-success", recoveryFingerprint, recoveryJson), cancellationToken);
                await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease, recovery.FileId,
                    recovery.ContentHash, recovery.Size, cancellationToken);
                await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                    operation.ObjectId!, recoveryAllocation.AllocationId, "dropbox", recoveryJson,
                    recovery.ContentHash, recovery.Size, cancellationToken);
                await authority.CompleteIntermediateAttemptAsync(recoveryAttempt.AttemptId, lease,
                    "{\"result\":\"remote predecessor retained\"}", cancellationToken);
            }

            // H7/G-2: the exact retained pre-replacement boundary. The predecessor recovery is verified and no
            // replacement request has been issued yet, so this is the point a process boundary leaves the
            // operation in its legitimate retained Validating recovery state.
            PhaseForTest?.Invoke("remote-update-recovery-retained-before-replacement");
            SyncDropboxArtifactPaths.RequireRecoveryContainerIfDropbox(pair, recoveryRoot.RootPath);
            await remoteStore.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
            var verifiedRecovery = await remoteStore.GetRecoveryAsync(recoveryRoot,
                recoveryAllocation.DeterministicName, cancellationToken);
            if (verifiedRecovery is null || !string.Equals(verifiedRecovery.FileId, recovery.FileId,
                    StringComparison.Ordinal) || !string.Equals(verifiedRecovery.Revision, recovery.Revision,
                    StringComparison.Ordinal) || verifiedRecovery.Size != remote.Size ||
                !string.Equals(verifiedRecovery.ContentHash, remote.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained Dropbox predecessor recovery object is not exact.");
            remote = await LoadExactRemoteAsync(path, remotePredecessor, cancellationToken);
            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(localSource, local, root, cancellationToken, pair.Provider);
            localSource.Position = 0;
            var uploadSession = await PrepareUploadSessionAsync(operation, localVersion, lease,
                cancellationToken);

            var updateFingerprint = Hash("update-remote", pair.Id, remote.Id!, remote.Revision!, path,
                localVersion.ObjectVersionId, localVersion.ContentHash!,
                localVersion.Size!.Value.ToString(CultureInfo.InvariantCulture), recovery.FileId,
                recovery.Revision);
            var updateAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, updateFingerprint,
                "dropbox-upload-update", cancellationToken);
            dispatched = true;
            PilotUploadResult uploaded;
            await using var uploadProgress = new SyncByteProgressStream(localSource, "Reading bytes for upload");
            await uploadProgress.StartedAsync();
            try
            {
                uploaded = uploadSession is null
                    ? await remoteStore.UploadAsync(path, uploadProgress, localVersion.ContentHash!,
                        remote.Revision, cancellationToken)
                    : await ((ISyncResumableRemoteStore)remoteStore).UploadResumableAsync(path, uploadProgress,
                        localVersion.ContentHash!, remote.Revision, uploadSession.Value.Checkpoint,
                        uploadSession.Value.Coordinator.PersistAsync, cancellationToken);
                if (uploadSession is not null)
                    await uploadSession.Value.Coordinator.CompleteAsync(cancellationToken);
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(updateAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, SourceJson(local), RemoteJson(remote),
                    "{\"classification\":\"replacement response unknown\"}",
                    Hash("update-unknown", updateFingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true, "The predecessor is retained, but the replacement response is unknown.");
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(updateAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, SourceJson(local), RemoteJson(remote),
                    JsonSerializer.Serialize(new { classification = "revision_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("update-not-applied", updateFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(updateAttempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"remote revision precondition rejected\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked,
                    true, "Dropbox rejected the exact-revision replacement without applying it." + " " + SyncActivityText.Failure(exception));
            }
            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(localSource, local, root, cancellationToken, pair.Provider);
            var successorJson = JsonSerializer.Serialize(new
            {
                uploaded.FileId,
                uploaded.Revision,
                uploaded.Size,
                uploaded.ContentHash,
                path
            });
            await authority.RecordReceiptAsync(updateAttempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, SourceJson(local), successorJson, successorJson,
                Hash("update-success", updateFingerprint, successorJson), cancellationToken);
            var remoteIdentity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot,
                uploaded.FileId, uploaded.Revision);
            var remoteVersionId = Id("version-dropbox", operation.ObjectId!, remoteIdentity,
                uploaded.ContentHash, uploaded.Size.ToString(CultureInfo.InvariantCulture));
            var remoteGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var remoteMembershipId = Id("membership-dropbox-effect", operationId, path, remoteVersionId);
            await authority.RegisterObjectVersionAsync(remoteVersionId, operation.ObjectId!, "dropbox",
                remoteIdentity, uploaded.ContentHash, uploaded.Size, cancellationToken);
            await authority.RegisterMembershipAsync(remoteMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, remoteGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operationId, localVersion.ObjectVersionId, remoteVersionId);
            await authority.CommitSuccessfulAttemptAsync(updateAttempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersion.ObjectVersionId, remoteVersionId, sourceMember.MembershipId,
                remoteMembershipId, "{\"result\":\"remote replacement verified\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"remote replacement complete; predecessor retained\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "Dropbox was conditionally replaced and its exact predecessor remains retained.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<(SyncAuthorityUploadSessionCoordinator Coordinator,
        SyncUploadSessionCheckpoint? Checkpoint)?> PrepareUploadSessionAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion sourceVersion,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var allocation = operation.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.UploadSession);
        var requiresSession = SyncUploadLimits.ForStore(remoteStore).RequiresSession(sourceVersion.Size,
            replacement: true);
        if (!requiresSession)
        {
            if (allocation is not null)
                throw new InvalidDataException(
                    "The retained replacement plan reserves a session that this destination's guarded transfer does not use. Verify the retained operation before reviewing a fresh plan; no replacement was uploaded by this attempt.");
            return null;
        }
        if (allocation is null || remoteStore is not ISyncResumableRemoteStore)
            throw new InvalidOperationException(
                "The large remote-replacement source has no resumable provider allocation or implementation.");
        var coordinator = new SyncAuthorityUploadSessionCoordinator(authority, allocation, sourceVersion, lease);
        var checkpoint = await coordinator.PrepareAsync(cancellationToken);
        return (coordinator, checkpoint);
    }

    internal async Task<PilotRemoteEntry> LoadExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        if (expected.Side != "dropbox" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained Dropbox predecessor authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedId = identity.RootElement.GetProperty("fileId").GetString();
        var expectedRevision = identity.RootElement.GetProperty("revision").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        await SyncByteProgressStream.ReportProcessAsync("Checking destination version", path,
            "Reading this file's exact current metadata to verify its identity, version, size and content hash before replacement; no recursive folder scan.");
        var current = await remoteStore.ReadEntryAsync(path, expectedId
            ?? throw new InvalidDataException("The retained replacement predecessor has no exact object identity."),
            cancellationToken);
        if (current.Kind != PilotEntryKind.File ||
            !string.Equals(current.RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Id, expectedId, StringComparison.Ordinal) ||
            !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal) ||
            current.Size != expected.Size ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityDestinationChangedException("The current Dropbox destination differs from the immutable predecessor.");
        return current;
    }

    internal static void RequireExactLocalSource(PilotLocalEntry current, SyncAuthorityObjectVersion expected)
    {
        if (expected.Side != "local" || current.VolumeSerialNumber is null || current.FileId is null ||
            expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local source authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedVolume = uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var expectedFile = ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var expectedWrite = DateTime.Parse(identity.RootElement.GetProperty("lastWriteUtc").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (current.VolumeSerialNumber != expectedVolume || current.FileId != expectedFile)
            throw new IOException("The exact local source identity differs from the immutable operation member.");
        if (current.Size != expected.Size || current.LastWriteUtc.ToUniversalTime() != expectedWrite.ToUniversalTime() ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthoritySourceChangedException("The exact local source content differs from the immutable operation member.");
    }

    private async Task<SyncArchiveResult> RequireVerifiedRecoveryAsync(SyncRemoteRecoveryRootBinding root,
        SyncAuthorityAllocation allocation, PilotRemoteEntry predecessor, CancellationToken cancellationToken)
    {
        SyncDropboxArtifactPaths.RequireRecoveryContainerIfDropbox(pair, root.RootPath);
        await remoteStore.VerifyRecoveryRootAsync(root, cancellationToken);
        var recovery = await remoteStore.GetRecoveryAsync(root, allocation.DeterministicName, cancellationToken);
        if (recovery is null || !string.Equals(recovery.FileId, allocation.BoundIdentity, StringComparison.Ordinal) ||
            recovery.Size != predecessor.Size || !string.Equals(recovery.ContentHash, predecessor.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The retained remote replacement predecessor is not exact.");
        return recovery;
    }

    internal async Task AdvanceAllocationAsync(string allocationId, SyncAuthorityLease lease, string identity,
        string contentHash, long size, CancellationToken cancellationToken)
    {
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Created,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Bound,
            boundIdentity: identity, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Verified,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
    }

    internal static string RecoveryJson(SyncArchiveResult recovery) => JsonSerializer.Serialize(new
    {
        recovery.FileId,
        recovery.Revision,
        recovery.RecoveryPath,
        recovery.Size,
        recovery.ContentHash
    });

    internal static string SourceJson(PilotLocalEntry source) => JsonSerializer.Serialize(new
    {
        source.RelativePath, source.VolumeSerialNumber, source.FileId, source.LastWriteUtc,
        source.Size, source.ContentHash
    });

    internal static string RemoteJson(PilotRemoteEntry remote) => JsonSerializer.Serialize(new
    {
        remote.RelativePath, remote.Id, remote.Revision, remote.Size, remote.ContentHash
    });

    internal static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    internal static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
