using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityCreateLocalExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    WindowsCloudFilesProvider? cloudFiles = null)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.CreateLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new InvalidOperationException("The requested authority operation is not a runnable local create for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local-create source has no exact version authority."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local-create source path is absent."));
        if (cloudFiles is not null)
            return await ExecutePlaceholderAsync(operation, sourceMember, destinationMember, sourceVersion,
                path, workerId, cancellationToken);
        if (ObjectKind(sourceMember) == "directory")
            return await ExecuteDirectoryAsync(operation, sourceMember, destinationMember, sourceVersion, path,
                workerId, cancellationToken);

        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("The pair has no verified same-volume control namespace.");
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        if (!string.Equals(stageAllocation.IntendedParentIdentity, transactionAllocation.AllocationId,
                StringComparison.Ordinal))
            throw new InvalidDataException("The stage allocation is not nested under the authorized transaction.");

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
            var remote = await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            RequireAbsentTarget(targetPath, selectedRoot);

            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            using var transaction = SyncLocalTransactionDirectory.CreateAuthorized(controlRoot,
                transactionAllocation.DeterministicName, out var securityHash);
            var transactionIdentity = Format(transaction.Identity);
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Created, boundIdentity: transactionIdentity,
                cancellationToken: cancellationToken);
            PhaseForTest?.Invoke("transaction-created-before-stage");
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, boundIdentity: transactionIdentity,
                contentHash: securityHash, cancellationToken: cancellationToken);
            PhaseForTest?.Invoke("transaction-bound-before-stage");
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, boundIdentity: transactionIdentity,
                contentHash: securityHash, cancellationToken: cancellationToken);
            PhaseForTest?.Invoke("transaction-verified-before-stage");

            var stagePath = Path.Combine(transaction.Path, stageAllocation.DeterministicName);
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            PilotDirectoryIdentity successorIdentity;
            SyncDownloadResult downloaded;
            DateTime successorLastWriteUtc;
            await using (var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath))
            {
                successorIdentity = stage.VerifySingleLink();
                await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Created, boundIdentity: Format(successorIdentity),
                    cancellationToken: cancellationToken);
                await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Bound, boundIdentity: Format(successorIdentity),
                    cancellationToken: cancellationToken);

                var requestFingerprint = Hash("create-local", pair.Id, pair.DropboxFolderId, remote.Id!,
                    remote.Revision!, path, sourceVersion.ObjectVersionId, sourceVersion.ContentHash!,
                    sourceVersion.Size!.Value.ToString(CultureInfo.InvariantCulture), transactionIdentity,
                    Format(successorIdentity));
                var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, requestFingerprint,
                    "local-create-from-dropbox-revision", cancellationToken);
                dispatched = true;
                await using var writer = new SyncByteProgressStream(stage.CreateGuardedWriteStream(),
                    "Downloading to verified staging", sourceVersion.Size,
                    readBackPhase: "Verifying downloaded content");
                await writer.StartedAsync();
                downloaded = await remoteStore.DownloadAsync(remote, writer, cancellationToken);
                await stage.Stream.FlushAsync(cancellationToken);
                stage.Stream.Flush(true);
                stage.VerifySameSingleLink(successorIdentity);
                stage.Stream.Position = 0;
                var contentHash = await SyncContentIdentity.ComputeAsync(pair.Provider, stage.Stream, cancellationToken);
                if (stage.Stream.Length != sourceVersion.Size ||
                    !string.Equals(contentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(downloaded.FileId, remote.Id, StringComparison.Ordinal) ||
                    !string.Equals(downloaded.Revision, remote.Revision, StringComparison.Ordinal))
                    throw new IOException("The downloaded stage does not match the immutable Dropbox source version.");
                await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Verified, boundIdentity: Format(successorIdentity),
                    contentHash: contentHash, size: stage.Stream.Length, cancellationToken: cancellationToken);
                await remoteStore.VerifyRootIdentityAsync(cancellationToken);
                var remoteAfterDownload = await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
                if (!SameRemote(remote, remoteAfterDownload))
                    throw new IOException("The Dropbox source changed before local commit.");
                RequireAbsentTarget(targetPath, selectedRoot);
                stage.RenameTo(selectedRoot, path);
                stage.VerifySameSingleLink(successorIdentity);
                successorLastWriteUtc = File.GetLastWriteTimeUtc(targetPath);
                PhaseForTest?.Invoke("target-promoted-before-receipt");

                var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.File,
                    successorIdentity.VolumeSerialNumber, successorIdentity.FileId, successorLastWriteUtc,
                    contentHash: contentHash, size: stage.Stream.Length);
                var destinationJson = JsonSerializer.Serialize(new
                {
                    path,
                    identity = localIdentity,
                    downloaded.Size,
                    downloaded.ContentHash
                });
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, JsonSerializer.Serialize(CurrentState(remote)),
                    destinationJson, destinationJson,
                    Hash("known-success", requestFingerprint, destinationJson), cancellationToken);

                PhaseForTest?.Invoke("receipt-before-version-registration");

                var localVersionId = Id("version-local", operation.ObjectId!, localIdentity,
                    downloaded.ContentHash, downloaded.Size.ToString(CultureInfo.InvariantCulture));
                var localGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                    path, cancellationToken);
                var localMembershipId = Id("membership-local-effect", operationId, path, localVersionId);
                await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                    localIdentity, downloaded.ContentHash, downloaded.Size, cancellationToken);
                await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                    destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
                PhaseForTest?.Invoke("membership-before-commit");
                var pointId = Id("syncpoint-effect", operationId, localVersionId, sourceVersion.ObjectVersionId);
                await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                    operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, localMembershipId,
                    sourceMember.MembershipId, "{\"result\":\"local create verified\"}", cancellationToken);
            }

            PhaseForTest?.Invoke("commit-before-cleanup");

            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            if (Directory.EnumerateFileSystemEntries(transaction.Path).Any())
                throw new IOException("The verified transaction directory retains an unexpected artifact.");
            transaction.DeleteOnClose();
            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
            await authority.CompleteOperationAsync(lease, "{\"result\":\"local create complete\"}",
                cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox revision was staged, promoted into an absent local target, and verified.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecutePlaceholderAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion sourceVersion, string path,
        string workerId, CancellationToken cancellationToken)
    {
        if (cloudFiles is null) throw new InvalidOperationException("The Cloud Files provider is unavailable.");
        var directory = ObjectKind(sourceMember) == "directory";
        var remote = directory
            ? await LoadExactRemoteDirectoryAsync(path, sourceVersion, cancellationToken)
            : await LoadExactRemoteAsync(path, sourceVersion, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            RequireAbsentTarget(targetPath, selectedRoot);
            var requestFingerprint = Hash("cloud-files-create-placeholder", pair.Id,
                pair.DropboxFolderId, remote.Id!, remote.Revision ?? "directory", path,
                sourceVersion.ObjectVersionId, sourceVersion.ContentHash ?? "none",
                sourceVersion.Size?.ToString(CultureInfo.InvariantCulture) ?? "0");
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                requestFingerprint, "cloud-files-create-placeholder", cancellationToken);
            dispatched = true;
            var placeholder = cloudFiles.CreatePlaceholder(remote);
            PhaseForTest?.Invoke("cloud-placeholder-created-before-receipt");
            if (!string.Equals(placeholder.Identity.PairId, pair.Id, StringComparison.Ordinal) ||
                !string.Equals(placeholder.Identity.RelativePath, path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(placeholder.Identity.DropboxId, remote.Id, StringComparison.Ordinal) ||
                !string.Equals(placeholder.Identity.DropboxRevision, remote.Revision, StringComparison.Ordinal) ||
                placeholder.Identity.LogicalSize != remote.Size ||
                !string.Equals(placeholder.Identity.ContentHash, remote.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("The Cloud Files placeholder differs from the immutable Dropbox source.");
            selectedRoot.VerifyBinding();
            var lastWriteUtc = directory
                ? Directory.GetLastWriteTimeUtc(targetPath)
                : File.GetLastWriteTimeUtc(targetPath);
            var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair,
                directory ? PilotEntryKind.Directory : PilotEntryKind.File,
                pair.LocalVolumeSerialNumber, placeholder.FileId, lastWriteUtc,
                placeholder.Identity.DropboxId, placeholder.Identity.DropboxRevision,
                contentHash: remote.ContentHash, size: remote.Size);
            var destinationJson = JsonSerializer.Serialize(new
            {
                path,
                kind = directory ? "directory" : "file",
                identity = localIdentity,
                size = remote.Size,
                contentHash = remote.ContentHash,
                availability = placeholder.State.Availability.ToString()
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, JsonSerializer.Serialize(CurrentState(remote)),
                destinationJson, destinationJson,
                Hash("known-success", requestFingerprint, destinationJson), cancellationToken);
            PhaseForTest?.Invoke("receipt-before-version-registration");
            var localVersionId = Id("version-local", operation.ObjectId!, localIdentity,
                remote.ContentHash ?? "none", remote.Size.ToString(CultureInfo.InvariantCulture));
            var localGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var localMembershipId = Id("membership-local-effect", operation.OperationId, path, localVersionId);
            await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                localIdentity, remote.ContentHash, remote.Size, cancellationToken);
            await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
            PhaseForTest?.Invoke("membership-before-commit");
            var pointId = Id("syncpoint-effect", operation.OperationId, localVersionId,
                sourceVersion.ObjectVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, localMembershipId,
                sourceMember.MembershipId, "{\"result\":\"cloud placeholder create verified\"}",
                cancellationToken);
            // The Cloud Files branch creates no ordinary stage/transaction/recovery artifact.
            PhaseForTest?.Invoke("commit-before-cleanup");
            // Its preauthorized allocations therefore remain Authorized and unbound.
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"cloud placeholder create complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox identity is represented by a verified online-only Windows placeholder.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion sourceVersion, string path,
        string workerId, CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var remote = await LoadExactRemoteDirectoryAsync(path, sourceVersion, cancellationToken);
            var targetPath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            RequireAbsentTarget(targetPath, selectedRoot);
            var requestFingerprint = Hash("create-local-directory", pair.Id, pair.DropboxFolderId,
                remote.Id!, path, sourceVersion.ObjectVersionId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                requestFingerprint, "local-create-directory", cancellationToken);
            dispatched = true;
            using var created = PilotDirectoryLease.CreateDescendant(selectedRoot, path);
            var createdIdentity = created.Identity;
            selectedRoot.VerifyBinding();
            if (!string.Equals(created.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The exact local directory did not reach the intended path.");
            PhaseForTest?.Invoke("directory-created-before-receipt");
            var localIdentity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, PilotEntryKind.Directory,
                createdIdentity.VolumeSerialNumber, createdIdentity.FileId,
                Directory.GetLastWriteTimeUtc(targetPath));
            var destinationJson = JsonSerializer.Serialize(new
            {
                path,
                kind = "directory",
                identity = localIdentity
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, JsonSerializer.Serialize(CurrentState(remote)),
                destinationJson, destinationJson,
                Hash("known-success", requestFingerprint, destinationJson), cancellationToken);
            PhaseForTest?.Invoke("receipt-before-version-registration");
            var localVersionId = Id("version-local", operation.ObjectId!, localIdentity, "none", "0");
            var localGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var localMembershipId = Id("membership-local-effect", operation.OperationId, path, localVersionId);
            await authority.RegisterObjectVersionAsync(localVersionId, operation.ObjectId!, "local",
                localIdentity, null, 0, cancellationToken);
            await authority.RegisterMembershipAsync(localMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, localGeneration, cancellationToken: cancellationToken);
            PhaseForTest?.Invoke("membership-before-commit");
            var pointId = Id("syncpoint-effect", operation.OperationId, localVersionId,
                sourceVersion.ObjectVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersionId, sourceVersion.ObjectVersionId, localMembershipId,
                sourceMember.MembershipId, "{\"result\":\"local directory create verified\"}",
                cancellationToken);
            PhaseForTest?.Invoke("commit-before-cleanup");
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local directory create complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox folder identity was created locally and verified.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<PilotRemoteEntry> LoadExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
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
            throw new IOException("The current Dropbox source identity differs from the immutable operation member.");
        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal) ||
            current.Size != expected.Size ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthoritySourceChangedException("The current Dropbox source content differs from the immutable operation member.");
        return current;
    }

    private async Task<PilotRemoteEntry> LoadExactRemoteDirectoryAsync(string path,
        SyncAuthorityObjectVersion expected, CancellationToken cancellationToken)
    {
        if (expected.Side != "dropbox" || expected.ContentHash is not null || expected.Size is not 0)
            throw new InvalidDataException("The retained Dropbox folder authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedId = identity.RootElement.GetProperty("fileId").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var current = await remoteStore.ReadEntryAsync(path, expectedId ??
            throw new InvalidDataException("The retained folder object ID is absent."), cancellationToken);
        if (current.Kind != PilotEntryKind.Directory ||
            !string.Equals(current.Id, expectedId, StringComparison.Ordinal))
            throw new IOException("The current Dropbox folder differs from the immutable operation member.");
        return current;
    }

    private static string ObjectKind(SyncAuthorityMember member)
    {
        using var state = JsonDocument.Parse(member.ExpectedStateJson);
        return state.RootElement.TryGetProperty("ObjectKind", out var pascal)
            ? pascal.GetString() ?? "file"
            : state.RootElement.TryGetProperty("objectKind", out var camel)
                ? camel.GetString() ?? "file"
                : "file";
    }

    private static void RequireAbsentTarget(string targetPath, PilotDirectoryLease root)
    {
        root.VerifyBinding();
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            throw new IOException("Create-local requires an absent exact target.");
    }

    private static bool SameRemote(PilotRemoteEntry left, PilotRemoteEntry right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Revision, right.Revision, StringComparison.Ordinal) &&
        left.Size == right.Size &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static object CurrentState(PilotRemoteEntry entry) => new
    {
        entry.RelativePath,
        entry.Id,
        entry.Revision,
        entry.Size,
        entry.ContentHash
    };

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
