using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityUpdateLocalReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    WindowsCloudFilesProvider? cloudFiles = null)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        SyncDropboxArtifactPaths.ValidateBoundRecoveryContainerIfPresent(pair);
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Attempts.Any(item => item.EffectKind.StartsWith("cloud-", StringComparison.Ordinal)) &&
            cloudFiles is null)
            throw new InvalidOperationException(
                "The interrupted Cloud Files replacement cannot be reconciled until its provider is connected.");
        if (operation.Kind != SyncAuthorityOperationKind.UpdateLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted local replacement.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The local replacement has no reconcilable attempt.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var remoteVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local replacement source version is absent."), cancellationToken);
        var predecessorVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The local replacement predecessor version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local replacement path is absent."));
        if (attempt.EffectKind is "dropbox-retain-placeholder-revision" or
            "cloud-files-update-placeholder")
            return await ReconcileCloudPlaceholderAsync(operation, attempt, sourceMember,
                destinationMember, remoteVersion, predecessorVersion, path, workerId,
                cancellationToken);
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        if (transactionAllocation.Status != SyncAuthorityAllocationStatus.Verified ||
            transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null ||
            stageAllocation.Status != SyncAuthorityAllocationStatus.Verified ||
            stageAllocation.BoundIdentity is null || stageAllocation.ContentHash is null || stageAllocation.Size is null)
            throw new IOException("The interrupted local replacement lacks exact transaction and stage authority.");
        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("Local replacement reconciliation requires a verified control namespace.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, selectedRoot);
            using var transaction = SyncLocalTransactionDirectory.OpenAuthorized(controlRoot,
                transactionAllocation.DeterministicName, transactionAllocation.BoundIdentity,
                transactionAllocation.ContentHash);
            var interruptedStage = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
            var interruptedTarget = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(interruptedStage,
                interruptedStage, interruptedTarget);
            var executor = new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority);
            // H8/G-1: the retained local stage, target and predecessor state are examined BEFORE a newer cloud
            // revision is allowed to end the reconciliation. A source that has legitimately advanced is a
            // replan condition, never a terminal dead end, and no stage reviewed against the superseded
            // revision is ever promoted over newer authority.
            if (await executor.TryMatchExactRemoteAsync(path, remoteVersion, cancellationToken) is null)
                return await ReconcileAdvancedSourceAsync(operation, attempt, path, transaction,
                    recoveryAllocation, predecessorVersion, selectedRoot, lease, cancellationToken);
            return attempt.EffectKind switch
            {
                "local-retain-predecessor" => await ReconcileRetentionThenPromoteAsync(operation, attempt,
                    sourceMember, destinationMember, remoteVersion, predecessorVersion, path, transaction,
                    stageAllocation, recoveryAllocation, selectedRoot, lease, cancellationToken),
                "local-promote-stage" => await ReconcilePromotionAsync(operation, attempt, sourceMember,
                    destinationMember, remoteVersion, predecessorVersion, path, transaction, stageAllocation,
                    recoveryAllocation, selectedRoot, lease, cancellationToken),
                _ => throw new InvalidDataException("The interrupted effect is outside local replacement choreography.")
            };
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileCloudPlaceholderAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityMember sourceMember, SyncAuthorityMember destinationMember,
        SyncAuthorityObjectVersion remoteVersion, SyncAuthorityObjectVersion predecessorVersion,
        string path, string workerId, CancellationToken cancellationToken)
    {
        if (cloudFiles is null)
            throw new InvalidOperationException(
                "The interrupted Cloud Files replacement cannot be reconciled until its provider is connected.");
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        var executor = new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority, cloudFiles);
        var successorRemote = await executor.LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
        var predecessorRemote = CloudPredecessor(predecessorVersion, path);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            var recovery = await remoteStore.GetRecoveryAsync(recoveryRoot,
                recoveryAllocation.DeterministicName, cancellationToken);
            if (attempt.EffectKind == "dropbox-retain-placeholder-revision")
            {
                if (recovery is null)
                {
                    var absent = "{\"reconciliation\":\"historical Recovery target absent\"}";
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied,
                        SyncAuthorityUpdateLocalExecutor.RemoteJson(predecessorRemote), "{}", absent,
                        SyncAuthorityUpdateLocalExecutor.Hash("cloud-recovery-not-applied",
                            attempt.RequestFingerprint, absent), cancellationToken);
                    await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                        SyncAuthorityAttemptStatus.VerifiedNotApplied,
                        "{\"result\":\"historical Recovery copy was not applied\"}",
                        cancellationToken);
                    return new SyncAuthorityExecutionResult(operation.OperationId,
                        SyncAuthorityOperationStatus.Ready, false,
                        "The historical Recovery copy is absent; the exact operation may be retried once.");
                }

                SyncAuthorityUpdateLocalExecutor.ValidateHistoricalRecovery(recovery,
                    predecessorRemote, recoveryRoot, recoveryAllocation.DeterministicName);
                var recoveryJson = SyncAuthorityUpdateLocalExecutor.RecoveryJson(recovery,
                    predecessorRemote);
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess,
                    SyncAuthorityUpdateLocalExecutor.RemoteJson(predecessorRemote), recoveryJson,
                    "{\"reconciliation\":\"exact historical Recovery object present\"}",
                    SyncAuthorityUpdateLocalExecutor.Hash("cloud-recovery-reconciled-success",
                        attempt.RequestFingerprint, recoveryJson), cancellationToken);
                if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Verified)
                    await executor.AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease,
                        recovery.FileId + ":" + recovery.Revision, recovery.ContentHash,
                        recovery.Size, cancellationToken);
                await authority.RegisterRecoveryArtifactAsync(
                    SyncAuthorityUpdateLocalExecutor.Id("recovery-artifact", operation.OperationId),
                    operation.OperationId, operation.ObjectId!, recoveryAllocation.AllocationId,
                    "dropbox", recoveryJson, recovery.ContentHash, recovery.Size,
                    cancellationToken);
                if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
                    await authority.CommitReconciledIntermediateAttemptAsync(attempt.AttemptId, lease,
                        "{\"result\":\"historical Recovery copy reconciled\"}",
                        cancellationToken);
                else
                    await authority.CompleteIntermediateAttemptAsync(attempt.AttemptId, lease,
                        "{\"result\":\"historical Recovery copy reconciled\"}",
                        cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The historical Recovery copy was verified; placeholder replacement can continue without recopying it.");
            }

            if (recovery is null || recoveryAllocation.Status != SyncAuthorityAllocationStatus.Verified)
                return await BlockAsync(operation.OperationId, attempt, lease,
                    "{\"result\":\"placeholder update lacks its verified historical Recovery copy\"}",
                    cancellationToken);
            SyncAuthorityUpdateLocalExecutor.ValidateHistoricalRecovery(recovery,
                predecessorRemote, recoveryRoot, recoveryAllocation.DeterministicName);
            if (!string.Equals(recoveryAllocation.BoundIdentity,
                    recovery.FileId + ":" + recovery.Revision, StringComparison.Ordinal) ||
                !string.Equals(recoveryAllocation.ContentHash, recovery.ContentHash,
                    StringComparison.OrdinalIgnoreCase) || recoveryAllocation.Size != recovery.Size)
                return await BlockAsync(operation.OperationId, attempt, lease,
                    "{\"result\":\"historical Recovery allocation identity changed\"}",
                    cancellationToken);

            var targetPath = PilotPath.ResolveContainedPath(pair.LocalRoot, path);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(targetPath, out var current) ||
                current is null || current.IsDirectory ||
                !string.Equals(current.Identity.PairId, pair.Id, StringComparison.Ordinal))
                return await BlockAsync(operation.OperationId, attempt, lease,
                    "{\"result\":\"cloud placeholder target is absent or has a different owner\"}",
                    cancellationToken);
            var predecessorFileId = LocalFileId(predecessorVersion);
            if (current.FileId != predecessorFileId)
                return await BlockAsync(operation.OperationId, attempt, lease,
                    "{\"result\":\"cloud placeholder file identity changed\"}",
                    cancellationToken);
            if (MatchesCloudIdentity(current.Identity, predecessorRemote))
            {
                var notApplied = "{\"reconciliation\":\"exact predecessor placeholder remains\"}";
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied,
                    SyncAuthorityUpdateLocalExecutor.RemoteJson(successorRemote), "{}", notApplied,
                    SyncAuthorityUpdateLocalExecutor.Hash("cloud-update-not-applied",
                        attempt.RequestFingerprint, notApplied), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"placeholder metadata update was not applied\"}",
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The exact predecessor placeholder remains; only the metadata update will be retried.");
            }
            if (!MatchesCloudIdentity(current.Identity, successorRemote))
                return await BlockAsync(operation.OperationId, attempt, lease,
                    "{\"result\":\"cloud placeholder carries neither predecessor nor successor authority\"}",
                    cancellationToken);
            return await CommitReconciledCloudSuccessAsync(operation, attempt, sourceMember,
                destinationMember, remoteVersion, path, current, successorRemote,
                recoveryAllocation, lease, cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> CommitReconciledCloudSuccessAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityMember sourceMember, SyncAuthorityMember destinationMember,
        SyncAuthorityObjectVersion remoteVersion, string path,
        WindowsCloudPlaceholderInfo current, PilotRemoteEntry successorRemote,
        SyncAuthorityAllocation recoveryAllocation, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        var targetPath = PilotPath.ResolveContainedPath(pair.LocalRoot, path);
        var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
            pair.LocalVolumeSerialNumber, current.FileId, File.GetLastWriteTimeUtc(targetPath),
            current.Identity.DropboxId, current.Identity.DropboxRevision,
            contentHash: successorRemote.ContentHash, size: successorRemote.Size);
        var successorJson = JsonSerializer.Serialize(new
        {
            path,
            identity = localIdentity,
            size = successorRemote.Size,
            contentHash = successorRemote.ContentHash,
            availability = current.State.Availability.ToString()
        });
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess,
            SyncAuthorityUpdateLocalExecutor.RemoteJson(successorRemote), successorJson,
            "{\"reconciliation\":\"exact successor placeholder present\"}",
            SyncAuthorityUpdateLocalExecutor.Hash("cloud-update-reconciled-success",
                attempt.RequestFingerprint, successorJson), cancellationToken);
        var localVersionId = SyncAuthorityUpdateLocalExecutor.Id("version-local",
            operation.ObjectId!, localIdentity, successorRemote.ContentHash!,
            successorRemote.Size.ToString(CultureInfo.InvariantCulture));
        var localGeneration = await authority.NextMembershipGenerationAsync(
            destinationMember.RootBindingId, path, cancellationToken);
        var localMembershipId = SyncAuthorityUpdateLocalExecutor.Id("membership-local-effect",
            operation.OperationId, path, localVersionId);
        await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
            localIdentity, successorRemote.ContentHash, successorRemote.Size, cancellationToken);
        await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
            destinationMember.RootBindingId, path, localGeneration,
            cancellationToken: cancellationToken);
        var pointId = SyncAuthorityUpdateLocalExecutor.Id("syncpoint-effect",
            operation.OperationId, localVersionId, remoteVersion.ObjectVersionId);
        await authority.CommitReconciledSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId,
            pair.Id, operation.ObjectId!, localVersionId, remoteVersion.ObjectVersionId,
            localMembershipId, sourceMember.MembershipId,
            "{\"result\":\"interrupted cloud placeholder update reconciled\"}",
            cancellationToken);
        // No ordinary stage/transaction was created by the placeholder branch.
        // Completing the operation closes execution without inventing resource effects.
        await authority.CompleteOperationAsync(lease,
            "{\"result\":\"reconciled cloud placeholder replacement complete\"}",
            cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId,
            SyncAuthorityOperationStatus.Completed, false,
            "The exact successor placeholder was committed without repeating the interrupted update.");
    }

    private static PilotRemoteEntry CloudPredecessor(SyncAuthorityObjectVersion predecessor,
        string path)
    {
        if (predecessor.Side != "local" || predecessor.ContentHash is null ||
            predecessor.Size is null)
            throw new InvalidDataException("The Cloud Files predecessor authority is incomplete.");
        using var identity = JsonDocument.Parse(predecessor.IdentityJson);
        var root = identity.RootElement;
        if (!root.TryGetProperty("cloudFiles", out var cloud))
            throw new InvalidDataException("The local predecessor has no Cloud Files authority.");
        var id = JsonString(cloud, "DropboxId", "dropboxId");
        var revision = JsonString(cloud, "DropboxRevision", "dropboxRevision");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(revision))
            throw new InvalidDataException("The local predecessor has no exact Dropbox revision authority.");
        return new PilotRemoteEntry(path, PilotEntryKind.File, id, revision,
            predecessor.Size.Value, predecessor.ContentHash);
    }

    private static ulong LocalFileId(SyncAuthorityObjectVersion predecessor)
    {
        using var identity = JsonDocument.Parse(predecessor.IdentityJson);
        return ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string? JsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static bool MatchesCloudIdentity(WindowsCloudFileIdentity identity,
        PilotRemoteEntry remote) =>
        string.Equals(identity.RelativePath, remote.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(identity.DropboxId, remote.Id, StringComparison.Ordinal) &&
        string.Equals(identity.DropboxRevision, remote.Revision, StringComparison.Ordinal) &&
        identity.LogicalSize == remote.Size &&
        string.Equals(identity.ContentHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// H8/G-1: the reviewed cloud source advanced past this replacement's immutable revision while the
    /// replacement was interrupted. The retained local state is examined FIRST: the exact owned predecessor is
    /// restored when the visible target is missing and its retention was not yet registered, the reviewed
    /// stage and every retained byte are preserved, foreign or newer local content is never overwritten, and
    /// the file is reported for a fresh comparison against the newer authority.
    /// </summary>
    private async Task<SyncAuthorityExecutionResult> ReconcileAdvancedSourceAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, string path,
        PilotDirectoryLease transaction, SyncAuthorityAllocation recoveryAllocation,
        SyncAuthorityObjectVersion predecessorVersion, PilotDirectoryLease selectedRoot, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
        var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            return await ReportAdvancedSourceAsync(operation, attempt, lease,
                "{\"result\":\"reviewed cloud source advanced; the visible local target and every retained artifact were preserved exactly as found\",\"class\":\"target-present\"}",
                "The visible target and every retained artifact were preserved exactly as found, so no local " +
                "content was overwritten or deleted.", cancellationToken);
        if (!File.Exists(recoveryPath))
            return await ReportAdvancedSourceAsync(operation, attempt, lease,
                "{\"result\":\"reviewed cloud source advanced; no exact owned predecessor is retained to restore\",\"class\":\"predecessor-absent\"}",
                "No exact owned predecessor is retained at its recovery location, so nothing could be restored " +
                "and nothing was deleted.", cancellationToken);
        if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified)
            return await ReportAdvancedSourceAsync(operation, attempt, lease,
                "{\"result\":\"reviewed cloud source advanced; the verified owned predecessor recovery artifact is retained untouched\",\"class\":\"recovery-verified-retained\"}",
                "The verified owned predecessor copy is retained under its registered recovery artifact and was " +
                "left exactly in place, because restoring it would have consumed that retained evidence.",
                cancellationToken);
        // The interruption landed after the owned predecessor was moved into its recovery location but before
        // that retention was registered, so the exact same object is moved back to the visible target rather
        // than leaving the file absent. Its retained identity, content hash and size are proved first, and the
        // reviewed stage stays where it is.
        await using (var recovery = await OpenExactFile(pair.Provider, recoveryPath,
                   recoveryAllocation.BoundIdentity ?? IdentityFromLocalVersion(predecessorVersion),
                   predecessorVersion.ContentHash, predecessorVersion.Size))
        {
            if (!string.Equals(recovery.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "The retained local predecessor is not the exact unregistered recovery object.");
            recovery.RenameTo(selectedRoot, path);
        }
        await using (var restored = SyncLocalFileAuthority.OpenExistingExclusive(targetPath))
            SyncAuthorityUpdateLocalExecutor.VerifyExactPredecessor(pair.Provider, restored, targetPath,
                predecessorVersion, selectedRoot);
        return await ReportAdvancedSourceAsync(operation, attempt, lease,
            "{\"result\":\"reviewed cloud source advanced; the exact owned predecessor was restored to the visible target and the reviewed stage retained\",\"class\":\"predecessor-restored\"}",
            "The exact owned predecessor was restored to the visible target from its retained bytes, so no " +
            "local file was lost.", cancellationToken);
    }

    /// <summary>
    /// Records one exact retained refusal for an interrupted local replacement whose reviewed cloud source
    /// advanced, through the same append-only receipt and attempt reconciliation the other ambiguity paths use,
    /// and returns the precise file outcome.
    /// </summary>
    private async Task<SyncAuthorityExecutionResult> ReportAdvancedSourceAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        string detail, string consequence, CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", detail,
            SyncAuthorityUpdateLocalExecutor.Hash("local-update-advanced-source", detail), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            detail, cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Blocked,
            false, "The reviewed cloud source advanced past the revision this local replacement was reviewed " +
            "against, so no stale reviewed content was promoted. " + consequence +
            " A fresh comparison of the current file is required.");
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileRetentionThenPromoteAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion,
        SyncAuthorityObjectVersion predecessorVersion, string path, PilotDirectoryLease transaction,
        SyncAuthorityAllocation stageAllocation, SyncAuthorityAllocation recoveryAllocation,
        PilotDirectoryLease selectedRoot, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
        var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
        if (File.Exists(recoveryPath) && !File.Exists(targetPath))
        {
            await using var recovery = await OpenExactFile(pair.Provider, recoveryPath,
                recoveryAllocation.BoundIdentity ?? IdentityFromLocalVersion(predecessorVersion),
                predecessorVersion.ContentHash, predecessorVersion.Size);
            var recoveryIdentity = SyncAuthorityUpdateLocalExecutor.Format(recovery.VerifySingleLink());
            var recoveryJson = LocalArtifactJson(recoveryPath, recoveryIdentity, recovery.Stream.Length,
                predecessorVersion.ContentHash!);
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess,
                SyncAuthorityUpdateLocalExecutor.LocalVersionJson(predecessorVersion), recoveryJson, recoveryJson,
                SyncAuthorityUpdateLocalExecutor.Hash("local-recovery-reconciled-success", recoveryJson),
                cancellationToken);
            if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Verified)
                await new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority).AdvanceAllocationAsync(
                    recoveryAllocation.AllocationId, lease, recoveryIdentity, predecessorVersion.ContentHash!,
                    predecessorVersion.Size, cancellationToken);
            await authority.RegisterRecoveryArtifactAsync(
                SyncAuthorityUpdateLocalExecutor.Id("recovery-artifact", operation.OperationId),
                operation.OperationId, operation.ObjectId!, recoveryAllocation.AllocationId, "local", recoveryJson,
                predecessorVersion.ContentHash!, predecessorVersion.Size!.Value, cancellationToken);
            if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
                await authority.CommitReconciledIntermediateAttemptAsync(attempt.AttemptId, lease,
                    "{\"result\":\"interrupted local predecessor retention verified\"}", cancellationToken);
            else
                await authority.CompleteIntermediateAttemptAsync(attempt.AttemptId, lease,
                    "{\"result\":\"interrupted local predecessor retention verified\"}", cancellationToken);
        }
        else if (!File.Exists(recoveryPath) && File.Exists(targetPath))
        {
            await using (var predecessor = SyncLocalFileAuthority.OpenExistingExclusive(targetPath))
                SyncAuthorityUpdateLocalExecutor.VerifyExactPredecessor(pair.Provider, predecessor, targetPath,
                    predecessorVersion, selectedRoot);
            var proof = "{\"result\":\"local predecessor retention not applied\"}";
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, proof, proof, proof,
                SyncAuthorityUpdateLocalExecutor.Hash("local-recovery-reconciled-not-applied", proof),
                cancellationToken);
            await authority.ReconcileIntermediateNotAppliedAttemptAsync(attempt.AttemptId, lease,
                proof, cancellationToken);
            await RetainPredecessorAsync(operation, predecessorVersion, path, transaction,
                recoveryAllocation, selectedRoot, lease, cancellationToken);
        }
        else
        {
            return await BlockAsync(operation.OperationId, attempt, lease,
                "{\"result\":\"local predecessor and recovery state is ambiguous\"}", cancellationToken);
        }
        return await PromoteFreshAsync(operation, sourceMember, destinationMember, remoteVersion,
            predecessorVersion, path, transaction, stageAllocation, recoveryAllocation, selectedRoot, lease,
            cancellationToken);
    }

    private async Task<SyncAuthorityExecutionResult> ReconcilePromotionAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion,
        SyncAuthorityObjectVersion predecessorVersion, string path, PilotDirectoryLease transaction,
        SyncAuthorityAllocation stageAllocation, SyncAuthorityAllocation recoveryAllocation,
        PilotDirectoryLease selectedRoot, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        await RequireRecoveryAsync(transaction, recoveryAllocation, predecessorVersion, cancellationToken, pair.Provider);
        var stagePath = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
        var stageExists = File.Exists(stagePath);
        var targetExists = File.Exists(targetPath);
        if (stageExists && targetExists || !stageExists && !targetExists)
            return await BlockAsync(operation.OperationId, attempt, lease,
                "{\"result\":\"local stage and target promotion state is ambiguous\"}", cancellationToken);
        if (targetExists)
            return await CommitPromotedTargetAsync(operation, attempt, sourceMember, destinationMember,
                remoteVersion, path, targetPath, stageAllocation, selectedRoot, lease, false, cancellationToken);

        await using (var stage = await OpenExactFile(pair.Provider, stagePath, stageAllocation.BoundIdentity,
                         remoteVersion.ContentHash, remoteVersion.Size))
        {
            var proof = "{\"result\":\"local stage promotion not applied\"}";
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, proof, proof, proof,
                SyncAuthorityUpdateLocalExecutor.Hash("local-promotion-reconciled-not-applied", proof),
                cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
        }
        return await PromoteFreshAsync(operation, sourceMember, destinationMember, remoteVersion,
            predecessorVersion, path, transaction, stageAllocation, recoveryAllocation, selectedRoot, lease,
            cancellationToken);
    }

    private async Task RetainPredecessorAsync(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityObjectVersion predecessorVersion, string path, PilotDirectoryLease transaction,
        SyncAuthorityAllocation recoveryAllocation, PilotDirectoryLease selectedRoot, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
        await using var predecessor = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
        SyncAuthorityUpdateLocalExecutor.VerifyExactPredecessor(pair.Provider, predecessor, targetPath, predecessorVersion,
            selectedRoot);
        if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Authorized)
            await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
        else if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Creating)
            throw new InvalidDataException(
                "The retried local predecessor recovery allocation is not authorized or already creating.");
        var fingerprint = SyncAuthorityUpdateLocalExecutor.Hash("local-recovery", operation.OperationId, path,
            predecessorVersion.ObjectVersionId, SyncAuthorityUpdateLocalExecutor.Format(predecessor.ReadIdentity()),
            SyncAuthorityUpdateLocalExecutor.Format(transaction.Identity), recoveryAllocation.DeterministicName);
        var retryAttempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
            "local-retain-predecessor", cancellationToken);
        predecessor.RenameTo(transaction, recoveryAllocation.DeterministicName);
        var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
        var identity = SyncAuthorityUpdateLocalExecutor.Format(predecessor.VerifySingleLink());
        predecessor.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(pair.Provider, predecessor.Stream, cancellationToken);
        if (predecessor.Stream.Length != predecessorVersion.Size ||
            !string.Equals(hash, predecessorVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The retried local predecessor retention is not exact.");
        var recoveryJson = LocalArtifactJson(recoveryPath, identity, predecessor.Stream.Length, hash);
        await authority.RecordReceiptAsync(retryAttempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess,
            SyncAuthorityUpdateLocalExecutor.LocalVersionJson(predecessorVersion), recoveryJson, recoveryJson,
            SyncAuthorityUpdateLocalExecutor.Hash("local-recovery-success", fingerprint, recoveryJson),
            cancellationToken);
        await new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority).AdvanceAllocationAsync(
            recoveryAllocation.AllocationId, lease, identity, hash, predecessor.Stream.Length, cancellationToken);
        await authority.RegisterRecoveryArtifactAsync(
            SyncAuthorityUpdateLocalExecutor.Id("recovery-artifact", operation.OperationId),
            operation.OperationId, operation.ObjectId!, recoveryAllocation.AllocationId, "local", recoveryJson,
            hash, predecessor.Stream.Length, cancellationToken);
        await authority.CompleteIntermediateAttemptAsync(retryAttempt.AttemptId, lease,
            "{\"result\":\"local predecessor retained after verified non-application\"}", cancellationToken);
    }

    private async Task<SyncAuthorityExecutionResult> PromoteFreshAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion,
        SyncAuthorityObjectVersion predecessorVersion, string path, PilotDirectoryLease transaction,
        SyncAuthorityAllocation stageAllocation, SyncAuthorityAllocation recoveryAllocation,
        PilotDirectoryLease selectedRoot, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var refreshed = await authority.LoadOperationAsync(operation.OperationId, cancellationToken);
        stageAllocation = refreshed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        recoveryAllocation = refreshed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        await RequireRecoveryAsync(transaction, recoveryAllocation, predecessorVersion, cancellationToken, pair.Provider);
        var executor = new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority);
        var remote = await executor.LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
        var stagePath = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
        var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new IOException("The local replacement target reappeared before promotion.");
        SyncAuthorityAttempt attempt;
        await using (var stage = await OpenExactFile(pair.Provider, stagePath, stageAllocation.BoundIdentity,
                         remoteVersion.ContentHash, remoteVersion.Size))
        {
            var fingerprint = SyncAuthorityUpdateLocalExecutor.Hash("local-promotion", operation.OperationId, path,
                remoteVersion.ObjectVersionId, stageAllocation.BoundIdentity!, recoveryAllocation.BoundIdentity ??
                throw new InvalidDataException("The local predecessor recovery identity is absent."));
            attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                "local-promote-stage", cancellationToken);
            stage.RenameTo(selectedRoot, path);
            stage.VerifySameSingleLink(SyncLocalRecovery.ParseSourceIdentity(stageAllocation.BoundIdentity!));
        }
        var result = await CommitPromotedTargetAsync(operation, attempt, sourceMember, destinationMember,
            remoteVersion, path, targetPath, stageAllocation, selectedRoot, lease, true, cancellationToken);
        _ = remote;
        return result;
    }

    private async Task<SyncAuthorityExecutionResult> CommitPromotedTargetAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion, string path,
        string targetPath, SyncAuthorityAllocation stageAllocation, PilotDirectoryLease selectedRoot,
        SyncAuthorityLease lease, bool dispatched, CancellationToken cancellationToken)
    {
        await new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority)
            .LoadExactRemoteAsync(path, remoteVersion, cancellationToken);
        await using var target = await OpenExactFile(pair.Provider, targetPath, stageAllocation.BoundIdentity,
            remoteVersion.ContentHash, remoteVersion.Size);
        var identity = target.VerifySingleLink();
        var lastWriteUtc = File.GetLastWriteTimeUtc(targetPath);
        var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
            identity.VolumeSerialNumber, identity.FileId, lastWriteUtc,
            contentHash: remoteVersion.ContentHash, size: target.Stream.Length);
        var successorJson = LocalArtifactJson(path, localIdentity, target.Stream.Length,
            remoteVersion.ContentHash!);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess, "{}", successorJson,
            "{\"reconciliation\":\"identity-bound promoted target present\"}",
            SyncAuthorityUpdateLocalExecutor.Hash("local-promotion-reconciled-success", successorJson),
            cancellationToken);
        var localVersionId = SyncAuthorityUpdateLocalExecutor.Id("version-local", operation.ObjectId!,
            localIdentity, remoteVersion.ContentHash!, target.Stream.Length.ToString(CultureInfo.InvariantCulture));
        var localMembershipId = SyncAuthorityUpdateLocalExecutor.Id("membership-local-effect",
            operation.OperationId, path, localVersionId);
        var generation = await authority.LoadMembershipGenerationAsync(localMembershipId, cancellationToken) ??
                         await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId, path,
                             cancellationToken);
        await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local", localIdentity,
            remoteVersion.ContentHash, target.Stream.Length, cancellationToken);
        await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
            destinationMember.RootBindingId, path, generation, cancellationToken: cancellationToken);
        var pointId = SyncAuthorityUpdateLocalExecutor.Id("syncpoint-effect", operation.OperationId,
            localVersionId, remoteVersion.ObjectVersionId);
        await authority.CommitReconciledSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
            operation.ObjectId!, localVersionId, remoteVersion.ObjectVersionId, localMembershipId,
            sourceMember.MembershipId, "{\"result\":\"interrupted local replacement reconciled\"}",
            cancellationToken);
        await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
            SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
        await authority.CompleteOperationAsync(lease,
            "{\"result\":\"reconciled local replacement complete; predecessor retained\"}", cancellationToken);
        selectedRoot.VerifyBinding();
        return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Completed,
            dispatched, dispatched
                ? "The verified stage was promoted after terminal reconciliation of the interrupted effect."
                : "The promoted local successor was committed from read-only reconciliation without replay.");
    }

    private static async Task<SyncLocalFileAuthority> OpenExactFile(string? contentProvider, string path, string? retainedIdentity,
        string? retainedHash, long? retainedSize)
    {
        if (retainedIdentity is null || retainedHash is null || retainedSize is null)
            throw new InvalidDataException("The retained local artifact authority is incomplete.");
        var result = SyncLocalFileAuthority.OpenExistingExclusive(path);
        try
        {
            if (result.VerifySingleLink() != SyncLocalRecovery.ParseSourceIdentity(retainedIdentity) ||
                result.Stream.Length != retainedSize)
                throw new IOException("The retained local artifact identity or size changed.");
            result.Stream.Position = 0;
            var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  result.Stream, CancellationToken.None);
            if (!string.Equals(hash, retainedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained local artifact content changed.");
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    private static async Task RequireRecoveryAsync(PilotDirectoryLease transaction,
        SyncAuthorityAllocation allocation, SyncAuthorityObjectVersion predecessor,
        CancellationToken cancellationToken, string? contentProvider)
    {
        if (allocation.Status != SyncAuthorityAllocationStatus.Verified)
            throw new IOException("The local predecessor recovery is not verified.");
        var path = Path.Combine(transaction.Path, allocation.DeterministicName);
        await using var recovery = await OpenExactFile(contentProvider, path, allocation.BoundIdentity,
            predecessor.ContentHash, predecessor.Size);
        transaction.VerifyBinding();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<SyncAuthorityExecutionResult> BlockAsync(string operationId, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, string detail, CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", detail,
            SyncAuthorityUpdateLocalExecutor.Hash("local-update-reconciled-conflict", detail),
            cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            detail, cancellationToken);
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
            "The interrupted local replacement is ambiguous; every artifact was preserved for review.");
    }

    private static string LocalArtifactJson(string path, string identity, long size, string contentHash) =>
        JsonSerializer.Serialize(new { path, identity, size, contentHash });

    private static string IdentityFromLocalVersion(SyncAuthorityObjectVersion version)
    {
        if (version.Side != "local")
            throw new InvalidDataException("The retained predecessor is not a local version.");
        using var identity = JsonDocument.Parse(version.IdentityJson);
        var volume = uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return SyncAuthorityUpdateLocalExecutor.Format(new PilotDirectoryIdentity(volume, file));
    }
}
