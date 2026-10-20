using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityDeleteLocalExecutor(
    SyncPair pair,
    SyncAuthorityDatabase authority,
    ISyncRemoteStore? remoteStore = null)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.DeleteLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new InvalidOperationException("The requested authority operation is not a runnable local deletion for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local deletion source version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local deletion path is absent."));
        var completedMove = SyncAuthorityCompletedCopyMove.Parse(operation.PreconditionsJson);
        // Ordinary deletions are planned from a baseline synchronization point; a reviewed first-sync
        // removal has none (its consent is the reviewed choice), so its provider deletion is re-verified below.
        var reviewedFirstSyncDeletion = completedMove is null &&
            System.Text.Json.Nodes.JsonNode.Parse(operation.PreconditionsJson)?["synchronizationPointId"] is null;
        var tombstoneId = completedMove is null ? TombstoneId(operation.PreconditionsJson) : "completed-copy-membership-retirement";
        if (completedMove is not null && (completedMove.OldPath != path || completedMove.OldVersionId != sourceVersion.ObjectVersionId ||
            completedMove.ObjectId != operation.ObjectId || sourceVersion.ContentHash is null))
            throw new InvalidDataException("The completed-copy retirement does not bind this exact predecessor.");
        if (sourceVersion.ContentHash is null)
            return await ExecuteDirectoryAsync(operation, sourceVersion, path, tombstoneId, workerId,
                cancellationToken);
        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("Local deletion requires a verified same-volume control namespace.");
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        if (recoveryAllocation.IntendedParentIdentity != transactionAllocation.AllocationId)
            throw new InvalidDataException("The local deletion recovery allocation is outside its authorized transaction.");
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
            var sourcePath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
            await VerifyExactLocalAsync(pair.Provider, source, sourcePath, sourceVersion, selectedRoot, cancellationToken);
            await using var retainedCopy = completedMove is null ? null :
                await SyncAuthorityCompletedCopyMove.HoldVerifiedCopyAsync(pair, remoteStore, authority,
                    selectedRoot, completedMove, cancellationToken);

            if (reviewedFirstSyncDeletion)
            {
                // One re-check immediately before removal: the provider must still report this exact
                // path deleted after the local file's current last write, and the path must be absent.
                var evidence = remoteStore is null
                    ? null
                    : await remoteStore.LookupDeletionEvidenceAsync([path], cancellationToken);
                if (evidence is null || !evidence.TryGetValue(path, out var deleted) || deleted.LookupFailed ||
                    deleted.DeletedUtc is not { } deletedUtc ||
                    deletedUtc <= new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath)) ||
                    await remoteStore!.FindEntryAtPathAsync(path, cancellationToken) is not null)
                    throw new IOException("The provider deletion is no longer current; no local file was removed.");
            }
            using var transaction = await OpenOrCreateTransactionAsync(controlRoot, transactionAllocation, lease,
                cancellationToken);
            var transactionIdentity = Format(transaction.Identity);
            if (recoveryAllocation.Status == SyncAuthorityAllocationStatus.Authorized)
                await authority.TransitionAllocationAsync(recoveryAllocation.AllocationId, lease,
                    SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
            else if (recoveryAllocation.Status != SyncAuthorityAllocationStatus.Creating)
                throw new InvalidOperationException("The local deletion recovery allocation is not retryable.");
            var sourceIdentity = source.VerifySingleLink();
            var fingerprint = Hash("delete-local", pair.Id, operation.ObjectId!, path,
                sourceVersion.ObjectVersionId, Format(sourceIdentity), transactionIdentity,
                recoveryAllocation.DeterministicName, tombstoneId);
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
                "local-retain-and-remove-membership", cancellationToken);
            dispatched = true;
            PhaseForTest?.Invoke("local-dispatched-before-retention");
            source.RenameTo(transaction, recoveryAllocation.DeterministicName);
            PhaseForTest?.Invoke("local-retained-before-receipt");
            source.VerifySameSingleLink(sourceIdentity);
            var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
            source.Stream.Position = 0;
            var contentHash = await SyncContentIdentity.ComputeAsync(pair.Provider, source.Stream, cancellationToken);
            if (source.Stream.Length != sourceVersion.Size ||
                !string.Equals(contentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source.FinalPath, recoveryPath, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(sourcePath) || Directory.Exists(sourcePath))
                throw new IOException("The local deletion recovery effect does not match its immutable preimage.");
            var recoveryJson = JsonSerializer.Serialize(new
            {
                path = recoveryPath,
                identity = Format(sourceIdentity),
                size = source.Stream.Length,
                contentHash
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(sourceVersion),
                "{\"present\":false}", recoveryJson,
                Hash("delete-local-success", fingerprint, recoveryJson), cancellationToken);
            await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease, Format(sourceIdentity),
                contentHash, source.Stream.Length, cancellationToken);
            await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                operation.ObjectId!, recoveryAllocation.AllocationId, "local", recoveryJson, contentHash,
                source.Stream.Length, cancellationToken);
            if (completedMove is null)
                await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                    pair.Id, operation.ObjectId!, "{\"result\":\"local membership removed; predecessor retained\"}", cancellationToken);
            else
                await authority.CommitSuccessfulAuxiliaryAttemptAsync(attempt.AttemptId, lease,
                    "{\"result\":\"obsolete membership retired after completed cloud move; accepted copy and predecessor retained\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local deletion complete; predecessor retained\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact local file was removed from the sync root by retaining it in the protected transaction.");
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
        if (sourceVersion.Side != "local" || sourceVersion.ContentHash is not null || sourceVersion.Size is not 0)
            throw new InvalidDataException("The retained local directory deletion authority is incomplete.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var sourcePath = PilotPath.ResolveContainedPath(root.Path, path);
            var identity = DirectoryIdentity(sourceVersion);
            string fingerprint;
            SyncAuthorityAttempt attempt;
            using (var source = PilotDirectoryLease.OpenOwnedDirectory(sourcePath, identity))
            {
                if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
                    throw new IOException(
                        "The directory deletion waits until every included descendant is recoverably retired.");
                fingerprint = Hash("delete-local-directory", pair.Id, operation.ObjectId!, path,
                    sourceVersion.ObjectVersionId, Format(identity), tombstoneId);
                attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                    fingerprint, "local-directory-delete", cancellationToken);
                dispatched = true;
                var recoveryArtifactId = Id("recovery-artifact", operation.OperationId);
                var recoveryJson = JsonSerializer.Serialize(new
                {
                    path,
                    originalPath = path,
                    kind = "directory",
                    recipe = "recreate_empty_directory",
                    deletedIdentity = Format(identity),
                    provenance = "pre_delete_directory_recipe"
                });
                await authority.RegisterRecoveryArtifactAsync(recoveryArtifactId, operation.OperationId,
                    operation.ObjectId!, null, "local", recoveryJson,
                    Hash("directory-recovery", operation.OperationId, path, Format(identity)), 0,
                    cancellationToken);
                source.DeleteOnClose();
                PhaseForTest?.Invoke("local-directory-delete-marked-before-receipt");
            }
            root.VerifyBinding();
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
                throw new IOException("The exact empty local directory remains after deletion.");
            var resultJson = JsonSerializer.Serialize(new
            {
                path,
                kind = "directory",
                deletedIdentity = Format(identity),
                tombstoneId,
                recoveryArtifactId = Id("recovery-artifact", operation.OperationId)
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(sourceVersion),
                "{\"present\":false}", resultJson,
                Hash("delete-local-directory-success", fingerprint, resultJson), cancellationToken);
            await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                pair.Id, operation.ObjectId!,
                "{\"result\":\"empty local directory membership removed\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local directory deletion complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact empty NTFS directory identity was removed after a durable recreate recipe was retained.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    internal static PilotDirectoryIdentity DirectoryIdentity(SyncAuthorityObjectVersion version)
    {
        if (version.Side != "local" || version.ContentHash is not null || version.Size is not 0)
            throw new InvalidDataException("The retained local directory identity is incomplete.");
        using var identity = JsonDocument.Parse(version.IdentityJson);
        return new PilotDirectoryIdentity(
            uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private async Task<PilotDirectoryLease> OpenOrCreateTransactionAsync(PilotDirectoryLease controlRoot,
        SyncAuthorityAllocation allocation, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (allocation.Status == SyncAuthorityAllocationStatus.Verified)
        {
            if (allocation.BoundIdentity is null || allocation.ContentHash is null)
                throw new InvalidDataException("The retained local deletion transaction binding is incomplete.");
            return SyncLocalTransactionDirectory.OpenAuthorized(controlRoot, allocation.DeterministicName,
                allocation.BoundIdentity, allocation.ContentHash);
        }
        if (allocation.Status != SyncAuthorityAllocationStatus.Authorized)
            throw new InvalidOperationException("The local deletion transaction allocation is not retryable.");
        await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
            SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
        var transaction = SyncLocalTransactionDirectory.CreateAuthorized(controlRoot,
            allocation.DeterministicName, out var securityHash);
        try
        {
            await AdvanceAllocationAsync(allocation.AllocationId, lease, Format(transaction.Identity),
                securityHash, null, cancellationToken);
            return transaction;
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    private static async Task VerifyExactLocalAsync(string? contentProvider, SyncLocalFileAuthority current, string sourcePath,
        SyncAuthorityObjectVersion expected, PilotDirectoryLease root, CancellationToken cancellationToken)
    {
        if (expected.Side != "local" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local deletion authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var volume = uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (current.VerifySingleLink() != new PilotDirectoryIdentity(volume, file) ||
            current.Stream.Length != expected.Size ||
            !string.Equals(current.FinalPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local deletion source differs from the immutable member identity.");
        current.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  current.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local deletion source bytes differ from the immutable member version.");
        root.VerifyBinding();
    }

    private async Task AdvanceAllocationAsync(string allocationId, SyncAuthorityLease lease, string identity,
        string contentHash, long? size, CancellationToken cancellationToken)
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

    private static string LocalVersionJson(SyncAuthorityObjectVersion version) => JsonSerializer.Serialize(new
    {
        version.ObjectVersionId, version.IdentityJson, version.ContentHash, version.Size
    });

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
