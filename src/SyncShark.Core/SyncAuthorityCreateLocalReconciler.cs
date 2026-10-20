using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityCreateLocalReconciler(
    SyncPair pair,
    SyncAuthorityDatabase authority,
    WindowsCloudFilesProvider? cloudFiles = null)
{
    internal static bool HasUnissuedPreparation(SyncAuthorityOperationSnapshot operation) =>
        operation.Kind == SyncAuthorityOperationKind.CreateLocal &&
        operation.Status == SyncAuthorityOperationStatus.Leased && operation.Attempts.Count == 0 &&
        operation.Allocations.Any(item => item.Status != SyncAuthorityAllocationStatus.Authorized);

    // Private preparation precedes StartDispatchedAttempt. Recovery must never
    // run the create executor again merely because no transfer receipt exists.
    internal async Task<SyncAuthorityExecutionResult> ReconcileUnissuedPreparationAsync(
        string operationId, string workerId, CancellationToken token)
    {
        if (cloudFiles is not null)
            throw new InvalidOperationException("Physical preparation recovery is not a placeholder operation.");
        await using var renewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId,
            workerId, SyncAuthorityLeasePolicy.LeaseDuration, token);
        token = renewal.WorkToken;
        var lease = renewal.Lease;
        try
        {
            var operation = await authority.LoadOperationAsync(operationId, token);
            if (operation.PairId != pair.Id || !HasUnissuedPreparation(operation) || operation.Allocations.Count != 2)
                throw new InvalidOperationException("The retained local preparation is not conclusively unissued.");
            var allocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
            var stage = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
            var controlBinding = pair.ControlNamespace ?? throw new IOException("The pair control root is not bound.");
            if (allocation.Status is not (SyncAuthorityAllocationStatus.Created or SyncAuthorityAllocationStatus.Bound or
                    SyncAuthorityAllocationStatus.Verified or SyncAuthorityAllocationStatus.Released) ||
                allocation.BoundIdentity is null || allocation.ControlNamespaceId != controlBinding.NamespaceId ||
                allocation.IntendedParentIdentity != allocation.ControlNamespaceId ||
                stage.Status != SyncAuthorityAllocationStatus.Authorized || stage.BoundIdentity is not null ||
                stage.ContentHash is not null || stage.Size is not null ||
                stage.IntendedParentIdentity != allocation.AllocationId || stage.DeterministicName != SyncLocalTransactionDirectory.StageName ||
                stage.ControlNamespaceId != allocation.ControlNamespaceId)
                throw new IOException("The private preparation authority is incomplete; all artifacts were preserved.");
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, selectedRoot);
            if (!allocation.DeterministicName.StartsWith(SyncLocalTransactionDirectory.Prefix, StringComparison.Ordinal) ||
                allocation.DeterministicName.Length != SyncLocalTransactionDirectory.Prefix.Length + 32 ||
                allocation.DeterministicName[SyncLocalTransactionDirectory.Prefix.Length..].Any(value => !char.IsAsciiHexDigit(value)) ||
                Path.GetFileName(allocation.DeterministicName) != allocation.DeterministicName)
                throw new IOException("The retained private preparation name is not accepted.");
            var path = Path.Combine(controlRoot.Path, allocation.DeterministicName);
            var absent = !PreparationPathExists(path);
            if (allocation.Status == SyncAuthorityAllocationStatus.Released)
            {
                if (!absent) throw new IOException("A released preparation path is occupied; its contents were preserved.");
            }
            else if (absent)
            {
                // An interrupted delete/release is recoverable only after the
                // retained identity and security authority were fully verified.
                if (allocation.Status != SyncAuthorityAllocationStatus.Verified || allocation.ContentHash is null)
                    throw new IOException("An incompletely bound private preparation is absent; authority was preserved.");
                await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Released, cancellationToken: token);
            }
            else
            {
                using (var transaction = allocation.Status == SyncAuthorityAllocationStatus.Created
                    ? PilotDirectoryLease.OpenOwnedDirectory(path, SyncLocalRecovery.ParseSourceIdentity(allocation.BoundIdentity))
                    : SyncLocalTransactionDirectory.OpenAuthorized(controlRoot, allocation.DeterministicName,
                        allocation.BoundIdentity, allocation.ContentHash ?? throw new IOException("Private preparation has no retained security hash.")))
                {
                    var securityHash = SyncLocalTransactionDirectory.RequireRestrictiveSecurity(path);
                    if (allocation.ContentHash is not null && allocation.ContentHash != securityHash)
                        throw new IOException("Private preparation permissions changed; the directory was preserved.");
                    if (Directory.EnumerateFileSystemEntries(path).Any())
                        throw new IOException("Private preparation contains an unexpected object; all contents were preserved.");
                    if (allocation.Status == SyncAuthorityAllocationStatus.Created)
                        await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                            SyncAuthorityAllocationStatus.Bound, contentHash: securityHash, cancellationToken: token);
                    if (allocation.Status is SyncAuthorityAllocationStatus.Created or SyncAuthorityAllocationStatus.Bound)
                        await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                            SyncAuthorityAllocationStatus.Verified, contentHash: securityHash, cancellationToken: token);
                    transaction.VerifyBinding();
                    controlRoot.VerifyBinding();
                    token.ThrowIfCancellationRequested();
                    transaction.DeleteOnClose();
                }
                controlRoot.VerifyBinding();
                if (PreparationPathExists(path))
                    throw new IOException("The exact empty private preparation was not removed.");
                await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Released, cancellationToken: token);
            }
            await authority.SupersedeReleasedLocalPreparationAsync(lease, pair.Id, token);
            return new(operationId, SyncAuthorityOperationStatus.Superseded, false,
                "The exact empty private preparation was released without a transfer. The old plan was retired; fresh observation can authorize a retry.");
        }
        finally { await renewal.ReleaseAsync(); }
    }

    private static bool PreparationPathExists(string path)
    {
        // Exists() also returns false on access errors. Only a real not-found
        // result can discharge retained private-allocation authority.
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.CreateLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.EffectUnknown))
            throw new InvalidOperationException("The requested authority operation is not an interrupted local create for this pair.");
        var attempt = operation.Attempts.Single(item => item.Status is SyncAuthorityAttemptStatus.RequestSent or
            SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown);
        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("The pair has no verified same-volume control namespace.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local-create source has no exact version authority."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local-create source path is absent."));
        if (attempt.EffectKind == "cloud-files-create-placeholder")
            return await ReconcilePlaceholderAsync(operation, attempt, sourceMember, destinationMember,
                sourceVersion, path, workerId, cancellationToken);
        if (ObjectKind(sourceMember) == "directory")
            return await ReconcileDirectoryAsync(operation, attempt, sourceMember, destinationMember, sourceVersion, path, workerId,
                cancellationToken);
        if (sourceVersion.ContentHash is null || sourceVersion.Size is null)
            throw new InvalidDataException("The local-create source content authority is incomplete.");
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        if (transactionAllocation.Status != SyncAuthorityAllocationStatus.Verified ||
            transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null ||
            stageAllocation.BoundIdentity is null)
            throw new InvalidOperationException("The interrupted local-create allocations are not fully identity-bound.");
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
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            var stagePath = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
            SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stagePath, stagePath, targetPath);
            var targetExists = File.Exists(targetPath) && !Directory.Exists(targetPath);
            var stageExists = File.Exists(stagePath) && !Directory.Exists(stagePath);
            if (targetExists && stageExists)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}",
                    "{\"reconciliation\":\"both stage and target exist\"}",
                    Hash("local-create-conflict", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"ambiguous stage and target identities retained\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                    "Both allocated stage and target exist; both were preserved for review.");
            }

            if (targetExists)
            {
                if (stageAllocation.Status != SyncAuthorityAllocationStatus.Verified)
                    throw new IOException("A promoted target exists without a verified stage allocation.");
                using var target = SyncLocalFileAuthority.OpenExistingExclusive(targetPath);
                var identity = target.VerifySingleLink();
                if (!string.Equals(Format(identity), stageAllocation.BoundIdentity, StringComparison.Ordinal))
                    throw new IOException("The local target is not the identity-bound promoted stage.");
                target.Stream.Position = 0;
                var hash = await SyncContentIdentity.ComputeAsync(pair.Provider, target.Stream, cancellationToken);
                if (target.Stream.Length != sourceVersion.Size ||
                    !string.Equals(hash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The promoted local target differs from the immutable source content.");
                var lastWriteUtc = File.GetLastWriteTimeUtc(targetPath);
                var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                    identity.VolumeSerialNumber, identity.FileId, lastWriteUtc,
                    contentHash: hash, size: target.Stream.Length);
                localIdentity = await RequireRecordedDestinationAsync(attempt, path, localIdentity,
                    sourceVersion, directory: false, cancellationToken);
                var destinationJson = JsonSerializer.Serialize(new
                {
                    path,
                    identity = localIdentity,
                    size = target.Stream.Length,
                    contentHash = hash
                });
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, "{}", destinationJson,
                    "{\"reconciliation\":\"identity-bound target present\"}",
                    Hash("local-create-success", attempt.RequestFingerprint, destinationJson), cancellationToken);
                var localVersionId = Id("version-local", operation.ObjectId!, localIdentity, hash,
                    target.Stream.Length.ToString(CultureInfo.InvariantCulture));
                var localMembershipId = Id("membership-local-effect", operationId, path, localVersionId);
                var localGeneration = await authority.LoadMembershipGenerationAsync(localMembershipId, cancellationToken)
                    ?? await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId, path, cancellationToken);
                await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                    localIdentity, hash, target.Stream.Length, cancellationToken);
                await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                    destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
                var pointId = Id("syncpoint-effect", operationId, localVersionId, sourceVersion.ObjectVersionId);
                await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                    operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, localMembershipId,
                    sourceMember.MembershipId, "{\"result\":\"interrupted local create reconciled\"}",
                    cancellationToken);
                await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
                if (Directory.EnumerateFileSystemEntries(transaction.Path).Any())
                    throw new IOException("The reconciled transaction retains an unexpected artifact.");
                transaction.DeleteOnClose();
                await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"reconciled local create complete\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed,
                    false, "The promoted target was verified and committed without repeating the effect.");
            }

            if (attempt.Status == SyncAuthorityAttemptStatus.ResponseReceived)
                throw new IOException("The recorded local-create target is now absent. Its successful receipt was preserved; a new observation is required, not a repeated download.");
            if (stageExists)
            {
                using (var stage = SyncLocalFileAuthority.OpenExistingExclusive(stagePath))
                {
                    if (!string.Equals(Format(stage.VerifySingleLink()), stageAllocation.BoundIdentity,
                            StringComparison.Ordinal))
                        throw new IOException("The retained local stage identity changed.");
                    stage.DeleteOnClose();
                }
            }
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                stageAllocation.Status == SyncAuthorityAllocationStatus.Verified
                    ? SyncAuthorityAllocationStatus.Released : SyncAuthorityAllocationStatus.Blocked,
                cancellationToken: cancellationToken);
            if (Directory.EnumerateFileSystemEntries(transaction.Path).Any())
                throw new IOException("The interrupted local-create transaction retains an unexpected artifact.");
            transaction.DeleteOnClose();
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                "{\"reconciliation\":\"target absent; owned partial stage removed\"}",
                Hash("local-create-not-applied", attempt.RequestFingerprint), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
                "{\"result\":\"target absent; fresh observation required\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                "No target effect exists; the operation is blocked for a fresh immutable plan.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcilePlaceholderAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityMember sourceMember, SyncAuthorityMember destinationMember,
        SyncAuthorityObjectVersion sourceVersion, string path, string workerId,
        CancellationToken cancellationToken)
    {
        if (cloudFiles is null)
            throw new InvalidOperationException(
                "The interrupted Cloud Files create cannot be reconciled until its provider is connected.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                if (attempt.Status == SyncAuthorityAttemptStatus.ResponseReceived)
                    throw new IOException("The recorded placeholder target is now absent; its receipt was preserved without repeating the create.");
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                    "{\"reconciliation\":\"cloud placeholder target absent\"}",
                    Hash("cloud-placeholder-not-applied", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"verified cloud placeholder absence\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The interrupted placeholder create was not applied and may be retried once.");
            }

            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(targetPath, out var placeholder) ||
                placeholder is null ||
                !string.Equals(placeholder.Identity.PairId, pair.Id, StringComparison.Ordinal) ||
                !MatchesSource(placeholder.Identity, sourceVersion, path))
            {
                var detail = "{\"reconciliation\":\"placeholder target occupied by a different identity\"}";
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", detail,
                    Hash("cloud-placeholder-conflict", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked, detail, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Blocked, false,
                    "The interrupted placeholder target is occupied by a different object and was preserved.");
            }

            var directory = ObjectKind(sourceMember) == "directory";
            var lastWriteUtc = directory
                ? Directory.GetLastWriteTimeUtc(targetPath)
                : File.GetLastWriteTimeUtc(targetPath);
            var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair,
                directory ? PilotEntryKind.Directory : PilotEntryKind.File,
                pair.LocalVolumeSerialNumber, placeholder.FileId, lastWriteUtc,
                placeholder.Identity.DropboxId, placeholder.Identity.DropboxRevision,
                contentHash: sourceVersion.ContentHash, size: sourceVersion.Size);
            localIdentity = await RequireRecordedDestinationAsync(attempt, path, localIdentity,
                sourceVersion, directory, cancellationToken);
            var destinationJson = JsonSerializer.Serialize(new
            {
                path,
                kind = directory ? "directory" : "file",
                identity = localIdentity,
                size = sourceVersion.Size,
                contentHash = sourceVersion.ContentHash,
                availability = placeholder.State.Availability.ToString()
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", destinationJson,
                "{\"reconciliation\":\"exact cloud placeholder present\"}",
                Hash("cloud-placeholder-success", attempt.RequestFingerprint, destinationJson),
                cancellationToken);
            var localVersionId = Id("version-local", operation.ObjectId!, localIdentity,
                sourceVersion.ContentHash ?? "none",
                (sourceVersion.Size ?? 0).ToString(CultureInfo.InvariantCulture));
            var localMembershipId = Id("membership-local-effect", operation.OperationId, path, localVersionId);
            var localGeneration = await authority.LoadMembershipGenerationAsync(localMembershipId, cancellationToken)
                ?? await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId, path, cancellationToken);
            await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                localIdentity, sourceVersion.ContentHash, sourceVersion.Size, cancellationToken);
            await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operation.OperationId, localVersionId,
                sourceVersion.ObjectVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, localMembershipId,
                sourceMember.MembershipId, "{\"result\":\"interrupted cloud placeholder reconciled\"}",
                cancellationToken);
            // No ordinary stage/transaction/recovery artifact was created by this branch.
            // Preserve those preauthorized allocations as Authorized and unbound.
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"reconciled cloud placeholder complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, false,
                "The exact existing Cloud Files placeholder was verified and committed without repeating it.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private static bool MatchesSource(WindowsCloudFileIdentity identity,
        SyncAuthorityObjectVersion sourceVersion, string path)
    {
        if (!string.Equals(identity.RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
            identity.LogicalSize != sourceVersion.Size ||
            !string.Equals(identity.ContentHash, sourceVersion.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            return false;
        using var sourceIdentity = JsonDocument.Parse(sourceVersion.IdentityJson);
        var root = sourceIdentity.RootElement;
        var id = root.TryGetProperty("fileId", out var fileId) ? fileId.GetString() : null;
        var revision = root.TryGetProperty("revision", out var remoteRevision)
            ? remoteRevision.GetString()
            : null;
        return string.Equals(identity.DropboxId, id, StringComparison.Ordinal) &&
               string.Equals(identity.DropboxRevision, revision, StringComparison.Ordinal);
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityMember sourceMember, SyncAuthorityMember destinationMember,
        SyncAuthorityObjectVersion sourceVersion, string path, string workerId,
        CancellationToken cancellationToken)
    {
        if (sourceVersion.Side != "dropbox" || sourceVersion.ContentHash is not null ||
            sourceVersion.Size is not 0)
            throw new InvalidDataException("The local-create folder source authority is incomplete.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            selectedRoot.VerifyBinding();
            if (attempt.Status == SyncAuthorityAttemptStatus.ResponseReceived)
            {
                // Unlike a pre-receipt directory, a retained successful receipt
                // binds the created physical identity. Presence alone is never enough.
                using var target = PilotDirectoryLease.Open(targetPath);
                var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair,
                    PilotEntryKind.Directory, target.Identity.VolumeSerialNumber, target.Identity.FileId,
                    Directory.GetLastWriteTimeUtc(targetPath));
                localIdentity = await RequireRecordedDestinationAsync(attempt, path, localIdentity,
                    sourceVersion, directory: true, cancellationToken);
                selectedRoot.VerifyBinding();
                var localVersionId = Id("version-local", operation.ObjectId!, localIdentity, "none", "0");
                var membershipId = Id("membership-local-effect", operation.OperationId, path, localVersionId);
                var generation = await authority.LoadMembershipGenerationAsync(membershipId, cancellationToken)
                    ?? await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId, path, cancellationToken);
                await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                    localIdentity, null, 0, cancellationToken);
                await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!, destinationMember.RootBindingId,
                    path, generation, cancellationToken: cancellationToken);
                await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease,
                    Id("syncpoint-effect", operation.OperationId, localVersionId, sourceVersion.ObjectVersionId),
                    pair.Id, operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, membershipId,
                    sourceMember.MembershipId, "{\"result\":\"receipted local directory reconciled\"}", cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"receipted local directory complete\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Completed, false,
                    "The receipt-bound existing folder was committed without creating it again.");
            }
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                var detail = Directory.Exists(targetPath)
                    ? "{\"reconciliation\":\"directory present without a prebound target identity\"}"
                    : "{\"reconciliation\":\"target occupied by a file\"}";
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", detail,
                    Hash("local-directory-create-conflict", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked, detail, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Blocked, false,
                    "The interrupted folder-create target is occupied and was preserved for review.");
            }
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                "{\"reconciliation\":\"directory target absent\"}",
                Hash("local-directory-create-not-applied", attempt.RequestFingerprint), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.VerifiedNotApplied,
                "{\"result\":\"verified directory target absence\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Ready, false,
                "The interrupted local folder create was not applied and may be retried once.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private async Task<string> RequireRecordedDestinationAsync(SyncAuthorityAttempt attempt, string path,
        string currentIdentity, SyncAuthorityObjectVersion source, bool directory, CancellationToken token)
    {
        if (attempt.Status != SyncAuthorityAttemptStatus.ResponseReceived) return currentIdentity;
        var receipts = await authority.LoadSuccessfulAttemptDestinationsAsync(attempt.AttemptId, token);
        if (receipts.Count == 0)
            throw new IOException("The received local-create response has no durable successful destination identity.");
        string? retainedIdentity = null;
        foreach (var json in receipts)
        {
            using var receipt = JsonDocument.Parse(json);
            var destination = receipt.RootElement;
            var identity = destination.GetProperty("identity").GetString()
                ?? throw new InvalidDataException("The successful local-create receipt has no physical identity.");
            if (!string.Equals(destination.GetProperty("path").GetString(), path, StringComparison.Ordinal) ||
                !SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(identity, currentIdentity,
                    directory ? null : source.ContentHash, directory ? null : source.Size))
                throw new IOException("The current local target differs from its retained successful create receipt; both were preserved for review.");
            if (directory)
            {
                if (destination.GetProperty("kind").GetString() != "directory")
                    throw new InvalidDataException("The retained successful create receipt does not identify a directory.");
            }
            else
            {
                var size = destination.TryGetProperty("size", out var lowerSize) ? lowerSize : destination.GetProperty("Size");
                var hash = destination.TryGetProperty("contentHash", out var lowerHash) ? lowerHash : destination.GetProperty("ContentHash");
                if (size.GetInt64() != source.Size ||
                    !string.Equals(hash.GetString(), source.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The retained successful create receipt differs from the immutable source content.");
            }
            retainedIdentity ??= identity;
        }
        // Preserve the executor's exact identity bytes/version key on replay,
        // including legacy identities without the newer redundant byte fields.
        return retainedIdentity!;
    }

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    private static string ObjectKind(SyncAuthorityMember member)
    {
        using var state = JsonDocument.Parse(member.ExpectedStateJson);
        return state.RootElement.TryGetProperty("ObjectKind", out var pascal)
            ? pascal.GetString() ?? "file"
            : state.RootElement.TryGetProperty("objectKind", out var camel)
                ? camel.GetString() ?? "file"
                : "file";
    }

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
