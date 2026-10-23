using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityUpdateLocalExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    WindowsCloudFilesProvider? cloudFiles = null)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.UpdateLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
                SyncAuthorityOperationStatus.Validating))
            throw new InvalidOperationException("The requested authority operation is not a runnable local replacement for this pair.");
        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("Local replacement requires a verified same-volume control namespace.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var remoteVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local replacement source version is absent."), cancellationToken);
        var localPredecessor = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The local replacement predecessor version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local replacement path is absent."));
        var targetPathCandidate = PilotPath.ResolveContainedPath(pair.LocalRoot, path);
        if (cloudFiles is not null &&
            WindowsCloudFilesProvider.TryReadOwnedPlaceholder(targetPathCandidate, out var cloudPredecessor) &&
            cloudPredecessor is not null && !cloudPredecessor.IsDirectory &&
            !cloudPredecessor.State.IsPinned &&
            cloudPredecessor.State.Availability != SyncCloudFileAvailability.AvailableOffline)
            return await ExecutePlaceholderUpdateAsync(operation, sourceMember, destinationMember,
                remoteVersion, localPredecessor, path, cloudPredecessor, workerId, cancellationToken);
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        if (stageAllocation.IntendedParentIdentity != transactionAllocation.AllocationId ||
            recoveryAllocation.IntendedParentIdentity != transactionAllocation.AllocationId)
            throw new InvalidDataException("Local replacement artifacts do not share the authorized transaction parent.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, selectedRoot);
            var remote = await LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            using var predecessor = OpenExactLocalPredecessor(targetPath, localPredecessor, selectedRoot, pair.Provider);

            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            using var transaction = SyncLocalTransactionDirectory.CreateAuthorized(controlRoot,
                transactionAllocation.DeterministicName, out var securityHash);
            var transactionIdentity = Format(transaction.Identity);
            await AdvanceAllocationAsync(transactionAllocation.AllocationId, lease, transactionIdentity,
                securityHash, null, cancellationToken);

            var stagePath = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            await using var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
            var stageIdentity = stage.VerifySingleLink();
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Created, boundIdentity: Format(stageIdentity),
                cancellationToken: cancellationToken);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: Format(stageIdentity),
                cancellationToken: cancellationToken);
            await using var writer = new SyncByteProgressStream(stage.CreateGuardedWriteStream(),
                "Downloading to verified staging", remote.Size,
                readBackPhase: "Verifying downloaded content");
            await writer.StartedAsync();
            var downloaded = await remoteStore.DownloadAsync(remote, writer, cancellationToken);
            await stage.Stream.FlushAsync(cancellationToken);
            stage.Stream.Flush(true);
            stage.VerifySameSingleLink(stageIdentity);
            stage.Stream.Position = 0;
            var stageHash = await SyncContentIdentity.ComputeAsync(pair.Provider, stage.Stream, cancellationToken);
            if (stage.Stream.Length != remoteVersion.Size ||
                !string.Equals(stageHash, remoteVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(downloaded.FileId, remote.Id, StringComparison.Ordinal) ||
                !string.Equals(downloaded.Revision, remote.Revision, StringComparison.Ordinal))
                throw new IOException("The local replacement stage differs from the immutable Dropbox source.");
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: Format(stageIdentity),
                contentHash: stageHash, size: stage.Stream.Length, cancellationToken: cancellationToken);

            remote = await LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
            VerifyExactPredecessor(pair.Provider, predecessor, targetPath, localPredecessor, selectedRoot);
            var recoveryFingerprint = Hash("local-recovery", operationId, path,
                localPredecessor.ObjectVersionId, Format(predecessor.ReadIdentity()),
                transactionIdentity, recoveryAllocation.DeterministicName);
            await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            var recoveryAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, recoveryFingerprint,
                "local-retain-predecessor", cancellationToken);
            dispatched = true;
            PhaseForTest?.Invoke("local-update-predecessor-retention-attempt-started");
            predecessor.RenameTo(transaction, recoveryAllocation.DeterministicName);
            PhaseForTest?.Invoke("local-update-predecessor-retained-before-receipt");
            var predecessorIdentity = predecessor.VerifySingleLink();
            var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
            predecessor.Stream.Position = 0;
            var predecessorHash = await SyncContentIdentity.ComputeAsync(pair.Provider, predecessor.Stream, cancellationToken);
            if (predecessor.Stream.Length != localPredecessor.Size ||
                !string.Equals(predecessorHash, localPredecessor.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(predecessor.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained local predecessor differs from its immutable preimage.");
            var recoveryJson = JsonSerializer.Serialize(new
            {
                path = recoveryPath,
                identity = Format(predecessorIdentity),
                size = predecessor.Stream.Length,
                contentHash = predecessorHash
            });
            await authority.RecordReceiptAsync(recoveryAttempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(localPredecessor), recoveryJson,
                recoveryJson, Hash("local-recovery-success", recoveryFingerprint, recoveryJson), cancellationToken);
            await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease, Format(predecessorIdentity),
                predecessorHash, predecessor.Stream.Length, cancellationToken);
            await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                operation.ObjectId!, recoveryAllocation.AllocationId, "local", recoveryJson, predecessorHash,
                predecessor.Stream.Length, cancellationToken);
            await authority.CompleteIntermediateAttemptAsync(recoveryAttempt.AttemptId, lease,
                "{\"result\":\"local predecessor retained\"}", cancellationToken);

            var promotionFingerprint = Hash("local-promotion", operationId, path, remoteVersion.ObjectVersionId,
                Format(stageIdentity), Format(predecessorIdentity));
            var promotionAttempt = await authority.StartDispatchedAttemptAsync(operationId, lease, promotionFingerprint,
                "local-promote-stage", cancellationToken);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
                throw new IOException("The local destination reappeared after predecessor retention.");
            stage.RenameTo(selectedRoot, path);
            PhaseForTest?.Invoke("local-update-stage-promoted-before-receipt");
            stage.VerifySameSingleLink(stageIdentity);
            var successorWriteUtc = File.GetLastWriteTimeUtc(targetPath);
            var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                stageIdentity.VolumeSerialNumber, stageIdentity.FileId, successorWriteUtc,
                contentHash: stageHash, size: stage.Stream.Length);
            var successorJson = JsonSerializer.Serialize(new
            {
                path,
                identity = localIdentity,
                size = stage.Stream.Length,
                contentHash = stageHash
            });
            await authority.RecordReceiptAsync(promotionAttempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), successorJson, successorJson,
                Hash("local-promotion-success", promotionFingerprint, successorJson), cancellationToken);
            var localVersionId = Id("version-local", operation.ObjectId!, localIdentity, stageHash,
                stage.Stream.Length.ToString(CultureInfo.InvariantCulture));
            var localGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var localMembershipId = Id("membership-local-effect", operationId, path, localVersionId);
            await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local", localIdentity,
                stageHash, stage.Stream.Length, cancellationToken);
            await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operationId, localVersionId, remoteVersion.ObjectVersionId);
            await authority.CommitSuccessfulAttemptAsync(promotionAttempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersionId, remoteVersion.ObjectVersionId, localMembershipId,
                sourceMember.MembershipId, "{\"result\":\"local replacement verified\"}", cancellationToken);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local replacement complete; predecessor retained\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox revision replaced the local file after its predecessor was retained.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecutePlaceholderUpdateAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion,
        SyncAuthorityObjectVersion localPredecessor, string path,
        WindowsCloudPlaceholderInfo cloudPredecessor, string workerId,
        CancellationToken cancellationToken)
    {
        if (cloudFiles is null) throw new InvalidOperationException("The Cloud Files provider is unavailable.");
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        if (localPredecessor.Side != "local" || localPredecessor.ContentHash is null ||
            localPredecessor.Size is null ||
            !string.Equals(cloudPredecessor.Identity.PairId, pair.Id, StringComparison.Ordinal) ||
            !string.Equals(cloudPredecessor.Identity.RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
            cloudPredecessor.Identity.LogicalSize != localPredecessor.Size ||
            !string.Equals(cloudPredecessor.Identity.ContentHash, localPredecessor.ContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(cloudPredecessor.Identity.DropboxId) ||
            string.IsNullOrWhiteSpace(cloudPredecessor.Identity.DropboxRevision))
            throw new IOException("The online-only placeholder differs from the immutable local predecessor.");
        using (var localIdentityDocument = JsonDocument.Parse(localPredecessor.IdentityJson))
        {
            var expectedFileId = ulong.Parse(localIdentityDocument.RootElement.GetProperty("fileId").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (expectedFileId != cloudPredecessor.FileId)
                throw new IOException("The online-only placeholder file identity changed after observation.");
        }
        var remote = await LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            var oldRemote = new PilotRemoteEntry(path, PilotEntryKind.File,
                cloudPredecessor.Identity.DropboxId, cloudPredecessor.Identity.DropboxRevision,
                cloudPredecessor.Identity.LogicalSize, cloudPredecessor.Identity.ContentHash,
                cloudPredecessor.Identity.ModifiedUtc);
            SyncArchiveResult archived;
            if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified)
            {
                archived = await remoteStore.GetRecoveryAsync(recoveryRoot,
                               recoveryAllocation.DeterministicName, cancellationToken)
                           ?? throw new IOException(
                               "The verified historical Recovery allocation is absent from Dropbox.");
                ValidateHistoricalRecovery(archived, oldRemote, recoveryRoot,
                    recoveryAllocation.DeterministicName);
                if (!string.Equals(recoveryAllocation.BoundIdentity,
                        archived.FileId + ":" + archived.Revision, StringComparison.Ordinal) ||
                    !string.Equals(recoveryAllocation.ContentHash, archived.ContentHash,
                        StringComparison.OrdinalIgnoreCase) || recoveryAllocation.Size != archived.Size)
                    throw new IOException(
                        "The historical Recovery object differs from its retained allocation authority.");
            }
            else
            {
                if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Authorized)
                    throw new IOException(
                        "The historical Recovery allocation is neither fresh nor fully verified.");
                var recoveryFingerprint = Hash("cloud-placeholder-recovery", operation.OperationId, path,
                    oldRemote.Id!, oldRemote.Revision!, localPredecessor.ObjectVersionId,
                    recoveryAllocation.DeterministicName);
                await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
                var recoveryAttempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                    recoveryFingerprint, "dropbox-retain-placeholder-revision", cancellationToken);
                dispatched = true;
                archived = await remoteStore.CopyRevisionToRecoveryAsync(oldRemote, recoveryRoot,
                    recoveryAllocation.DeterministicName, cancellationToken);
                ValidateHistoricalRecovery(archived, oldRemote, recoveryRoot,
                    recoveryAllocation.DeterministicName);
                var recoveryReceiptJson = RecoveryJson(archived, oldRemote);
                await authority.RecordReceiptAsync(recoveryAttempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(oldRemote), recoveryReceiptJson,
                    recoveryReceiptJson,
                    Hash("cloud-placeholder-recovery-success", recoveryFingerprint, recoveryReceiptJson),
                    cancellationToken);
                await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease,
                    archived.FileId + ":" + archived.Revision, archived.ContentHash, archived.Size,
                    cancellationToken);
                await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operation.OperationId),
                    operation.OperationId, operation.ObjectId!, recoveryAllocation.AllocationId, "dropbox",
                    recoveryReceiptJson, archived.ContentHash, archived.Size, cancellationToken);
                await authority.CompleteIntermediateAttemptAsync(recoveryAttempt.AttemptId, lease,
                    "{\"result\":\"historical placeholder revision retained\"}", cancellationToken);
            }
            var recoveryJson = RecoveryJson(archived, oldRemote);
            await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operation.OperationId),
                operation.OperationId, operation.ObjectId!, recoveryAllocation.AllocationId, "dropbox",
                recoveryJson, archived.ContentHash, archived.Size, cancellationToken);

            remote = await LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                    PilotPath.ResolveContainedPath(pair.LocalRoot, path), out var beforeUpdate) ||
                beforeUpdate is null || beforeUpdate.FileId != cloudPredecessor.FileId ||
                !string.Equals(beforeUpdate.Identity.DropboxRevision,
                    cloudPredecessor.Identity.DropboxRevision, StringComparison.Ordinal))
                throw new IOException("The placeholder changed before its metadata replacement.");
            var updateFingerprint = Hash("cloud-placeholder-update", operation.OperationId, path,
                remoteVersion.ObjectVersionId, remote.Id!, remote.Revision!, archived.FileId, archived.Revision);
            var updateAttempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                updateFingerprint, "cloud-files-update-placeholder", cancellationToken);
            dispatched = true;
            var updated = cloudFiles.UpdatePlaceholder(remote, dehydrate: true);
            PhaseForTest?.Invoke("cloud-placeholder-updated-before-receipt");
            if (updated.FileId != cloudPredecessor.FileId ||
                !string.Equals(updated.Identity.DropboxId, remote.Id, StringComparison.Ordinal) ||
                !string.Equals(updated.Identity.DropboxRevision, remote.Revision, StringComparison.Ordinal) ||
                updated.Identity.LogicalSize != remote.Size ||
                !string.Equals(updated.Identity.ContentHash, remote.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("The updated placeholder differs from the immutable Dropbox source.");
            var targetPath = PilotPath.ResolveContainedPath(pair.LocalRoot, path);
            var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                pair.LocalVolumeSerialNumber, updated.FileId, File.GetLastWriteTimeUtc(targetPath),
                updated.Identity.DropboxId, updated.Identity.DropboxRevision,
                contentHash: remote.ContentHash, size: remote.Size);
            var successorJson = JsonSerializer.Serialize(new
            {
                path,
                identity = localIdentity,
                size = remote.Size,
                contentHash = remote.ContentHash,
                availability = updated.State.Availability.ToString()
            });
            await authority.RecordReceiptAsync(updateAttempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(remote), successorJson,
                successorJson, Hash("cloud-placeholder-update-success", updateFingerprint, successorJson),
                cancellationToken);
            var localVersionId = Id("version-local", operation.ObjectId!, localIdentity,
                remote.ContentHash!, remote.Size.ToString(CultureInfo.InvariantCulture));
            var localGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var localMembershipId = Id("membership-local-effect", operation.OperationId, path, localVersionId);
            await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                localIdentity, remote.ContentHash, remote.Size, cancellationToken);
            await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operation.OperationId, localVersionId,
                remoteVersion.ObjectVersionId);
            await authority.CommitSuccessfulAttemptAsync(updateAttempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersionId, remoteVersion.ObjectVersionId, localMembershipId,
                sourceMember.MembershipId, "{\"result\":\"cloud placeholder replacement verified\"}",
                cancellationToken);
            // Placeholder updates never create the ordinary local stage/transaction.
            // Their unused reservations remain Authorized, not fictitiously Released.
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"cloud placeholder replacement complete; predecessor retained\"}",
                cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The cloud-only placeholder now targets the exact Dropbox revision; its predecessor is recoverable.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    internal async Task<PilotRemoteEntry> LoadExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        return await TryMatchExactRemoteAsync(path, expected, cancellationToken, reportSourceDrift: true) ??
               throw new IOException("The current Dropbox source differs from the immutable version.");
    }

    /// <summary>
    /// H8/G-1: the identical exact-revision check as <see cref="LoadExactRemoteAsync"/>, except that a source
    /// which has legitimately ADVANCED past the reviewed revision returns null instead of raising. The retained
    /// authority must still be complete and the provider root identity is still verified, so a provider,
    /// authority or ambiguity failure is never disguised as ordinary source drift: only a resolved entry that
    /// disagrees with the immutable reviewed version is reported as null.
    /// </summary>
    internal async Task<PilotRemoteEntry?> TryMatchExactRemoteAsync(string path,
        SyncAuthorityObjectVersion expected, CancellationToken cancellationToken, bool reportSourceDrift = false)
    {
        if (expected.Side != "dropbox" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained Dropbox source authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedId = identity.RootElement.GetProperty("fileId").GetString();
        var expectedRevision = identity.RootElement.GetProperty("revision").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var current = await remoteStore.ReadEntryAsync(path, expectedId ??
            throw new InvalidDataException("The retained source object ID is absent."), cancellationToken);
        if (current.Kind != PilotEntryKind.File ||
            !string.Equals(current.Id, expectedId, StringComparison.Ordinal))
            return null;
        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal) ||
            current.Size != expected.Size ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            if (reportSourceDrift)
                throw new SyncAuthoritySourceChangedException("The current Dropbox source content differs from the immutable version.");
            return null;
        }
        return current;
    }

    private static SyncLocalFileAuthority OpenExactLocalPredecessor(string targetPath,
        SyncAuthorityObjectVersion expected, PilotDirectoryLease root, string? contentProvider)
    {
        root.VerifyBinding();
        var result = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
        try
        {
            VerifyExactPredecessor(contentProvider, result, targetPath, expected, root);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static void VerifyExactPredecessor(string? contentProvider, SyncLocalFileAuthority current, string targetPath,
        SyncAuthorityObjectVersion expected, PilotDirectoryLease root)
    {
        if (expected.Side != "local" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local predecessor authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var volume = uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (current.VerifySingleLink() != new PilotDirectoryIdentity(volume, file) ||
            current.Stream.Length != expected.Size ||
            !string.Equals(current.FinalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityDestinationChangedException("The local destination differs from the immutable predecessor identity.");
        current.Stream.Position = 0;
        var hash = SyncContentIdentity.ComputeAsync(contentProvider,  current.Stream, CancellationToken.None).GetAwaiter().GetResult();
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityDestinationChangedException("The local destination bytes differ from the immutable predecessor.");
        root.VerifyBinding();
    }

    internal async Task AdvanceAllocationAsync(string allocationId, SyncAuthorityLease lease, string identity,
        string contentHash, long? size, CancellationToken cancellationToken)
    {
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Created,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Bound,
            boundIdentity: identity, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Verified,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
    }

    internal static string LocalVersionJson(SyncAuthorityObjectVersion version) => JsonSerializer.Serialize(new
    {
        version.ObjectVersionId, version.IdentityJson, version.ContentHash, version.Size
    });

    internal static string RemoteJson(PilotRemoteEntry remote) => JsonSerializer.Serialize(new
    {
        remote.RelativePath, remote.Id, remote.Revision, remote.Size, remote.ContentHash
    });

    internal static string RecoveryJson(SyncArchiveResult archived, PilotRemoteEntry source) =>
        JsonSerializer.Serialize(new
        {
            path = archived.RecoveryPath,
            identity = archived.FileId,
            revision = archived.Revision,
            archived.Size,
            archived.ContentHash,
            sourceFileId = source.Id,
            sourceRevision = source.Revision
        });

    internal static void ValidateHistoricalRecovery(SyncArchiveResult archived, PilotRemoteEntry source,
        SyncRemoteRecoveryRootBinding root, string deterministicName)
    {
        var expectedPath = PilotPath.NormalizeDropboxRoot(root.RootPath).TrimEnd('/') + "/" +
                           PilotPath.NormalizeTopLevelName(deterministicName);
        if (string.IsNullOrWhiteSpace(archived.FileId) || string.IsNullOrWhiteSpace(archived.Revision) ||
            !string.Equals(PilotPath.NormalizeDropboxRoot(archived.RecoveryPath), expectedPath,
                StringComparison.OrdinalIgnoreCase) || archived.Size != source.Size ||
            !string.Equals(archived.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The historical Dropbox Recovery object differs from its exact predecessor.");
    }

    internal static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    internal static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    internal static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
