using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityDeleteLocalReconciler(
    SyncPair pair,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.DeleteLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted local deletion.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The local deletion has no reconcilable attempt.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local deletion source version is absent."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The local deletion path is absent."));
        var completedMove = SyncAuthorityCompletedCopyMove.Parse(operation.PreconditionsJson);
        var tombstoneId = completedMove is null ? TombstoneId(operation.PreconditionsJson) : "completed-copy-membership-retirement";
        if (completedMove is not null && (completedMove.OldPath != path || completedMove.OldVersionId != sourceVersion.ObjectVersionId ||
            completedMove.ObjectId != operation.ObjectId || sourceVersion.ContentHash is null))
            throw new InvalidDataException("The retained completed-copy retirement does not bind this exact predecessor.");
        if (sourceVersion.ContentHash is null)
        {
            if (attempt.EffectKind != "local-directory-delete")
                throw new InvalidDataException("The interrupted effect is not a local directory deletion.");
            return await ReconcileDirectoryAsync(operation, attempt, sourceVersion, path, tombstoneId,
                workerId, cancellationToken);
        }
        if (attempt.EffectKind != "local-retain-and-remove-membership")
            throw new InvalidDataException("The interrupted effect is not local deletion retention.");
        var controlBinding = pair.ControlNamespace ??
                             throw new InvalidOperationException("Local deletion reconciliation requires a bound control namespace.");
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var recoveryAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            using var controlRoot = SyncControlNamespace.OpenLocal(controlBinding, selectedRoot);
            if (transactionAllocation.Status != SyncAuthorityAllocationStatus.Verified ||
                transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null)
                throw new IOException("The interrupted local deletion has no verified transaction authority.");
            using var transaction = SyncLocalTransactionDirectory.OpenAuthorized(controlRoot,
                transactionAllocation.DeterministicName, transactionAllocation.BoundIdentity,
                transactionAllocation.ContentHash);
            var sourcePath = PilotPath.ResolveContainedPath(selectedRoot.Path, path);
            var recoveryPath = Path.Combine(transaction.Path, recoveryAllocation.DeterministicName);
            var sourcePresent = File.Exists(sourcePath) || Directory.Exists(sourcePath);
            var recoveryPresent = File.Exists(recoveryPath) || Directory.Exists(recoveryPath);
            if (!sourcePresent && recoveryPresent)
            {
                await using var recovery = SyncLocalFileAuthority.OpenExistingExclusive(recoveryPath);
                var identity = await VerifyExactLocalAsync(pair.Provider, recovery, recoveryPath, sourceVersion, transaction,
                    cancellationToken);
                var recoveryJson = JsonSerializer.Serialize(new
                {
                    path = recoveryPath,
                    identity = Format(identity),
                    size = recovery.Stream.Length,
                    contentHash = sourceVersion.ContentHash
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(sourceVersion),
                        "{\"present\":false}", recoveryJson,
                        Hash("local-delete-reconciled-success", recoveryJson), cancellationToken);
                await AdvanceAllocationAsync(recoveryAllocation.AllocationId, lease, Format(identity),
                    sourceVersion.ContentHash!, recovery.Stream.Length, cancellationToken);
                await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                    operation.ObjectId!, recoveryAllocation.AllocationId, "local", recoveryJson,
                    sourceVersion.ContentHash!, recovery.Stream.Length, cancellationToken);
                if (completedMove is not null)
                    await authority.CommitSuccessfulAuxiliaryAttemptAsync(attempt.AttemptId, lease,
                        recoveryJson, cancellationToken);
                else if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
                    await authority.CommitReconciledDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, recoveryJson, cancellationToken);
                else
                    await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, recoveryJson, cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"local deletion reconciled from retained predecessor\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the local membership removed and its predecessor retained.");
            }

            if (sourcePresent && !recoveryPresent)
            {
                await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
                await VerifyExactLocalAsync(pair.Provider, source, sourcePath, sourceVersion, selectedRoot, cancellationToken);
                var proof = "{\"result\":\"local retention rename not applied\"}";
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied, LocalVersionJson(sourceVersion),
                        LocalVersionJson(sourceVersion), proof, Hash("local-delete-not-applied", proof),
                        cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready, false,
                    "The local rename was not applied; the existing verified transaction may be retried.");
            }

            var blocked = "{\"result\":\"local deletion membership/recovery state is ambiguous\"}";
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", blocked,
                    Hash("local-delete-ambiguous", blocked), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, blocked, cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                "Local deletion recovery is ambiguous; no further effect is allowed.");
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
        var expectedIdentity = SyncAuthorityDeleteLocalExecutor.DirectoryIdentity(sourceVersion);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var sourcePath = PilotPath.ResolveContainedPath(root.Path, path);
            root.VerifyBinding();
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                var proof = JsonSerializer.Serialize(new
                {
                    path,
                    kind = "directory",
                    deletedIdentity = Format(expectedIdentity),
                    tombstoneId,
                    result = "directory absent"
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                {
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(sourceVersion),
                        "{\"present\":false}", proof,
                        Hash("local-directory-delete-reconciled-success", proof), cancellationToken);
                    await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                else
                {
                    await authority.CommitReconciledDeletionAttemptAsync(attempt.AttemptId, lease, tombstoneId,
                        pair.Id, operation.ObjectId!, proof, cancellationToken);
                }
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"local directory deletion reconciled\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the exact empty local directory absent.");
            }
            if (Directory.Exists(sourcePath) && !File.Exists(sourcePath))
            {
                PilotDirectoryLease? source = null;
                try { source = PilotDirectoryLease.OpenOwnedDirectory(sourcePath, expectedIdentity); }
                catch (IOException) { }
                if (source is not null)
                {
                    using (source)
                    source.VerifyBinding();
                    var proof = "{\"result\":\"local directory deletion not applied\"}";
                    if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                            SyncAuthorityReceiptCertainty.KnownNotApplied, LocalVersionJson(sourceVersion),
                            LocalVersionJson(sourceVersion), proof,
                            Hash("local-directory-delete-not-applied", proof), cancellationToken);
                    await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                        SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                    return new SyncAuthorityExecutionResult(operation.OperationId,
                        SyncAuthorityOperationStatus.Ready, false,
                        "The empty directory deletion was not applied; one fenced retry is allowed.");
                }
            }
            var blocked = "{\"result\":\"local directory deletion identity is ambiguous\"}";
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", blocked,
                    Hash("local-directory-delete-ambiguous", blocked), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, blocked, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Blocked, false,
                "The local directory deletion identity is ambiguous; no retry is allowed.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private static async Task<PilotDirectoryIdentity> VerifyExactLocalAsync(string? contentProvider, SyncLocalFileAuthority current,
        string path, SyncAuthorityObjectVersion expected, PilotDirectoryLease root,
        CancellationToken cancellationToken)
    {
        if (expected.Side != "local" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local deletion authority is incomplete.");
        using var document = JsonDocument.Parse(expected.IdentityJson);
        var identity = new PilotDirectoryIdentity(
            uint.Parse(document.RootElement.GetProperty("volumeSerial").GetString()!, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture),
            ulong.Parse(document.RootElement.GetProperty("fileId").GetString()!, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture));
        if (current.VerifySingleLink() != identity || current.Stream.Length != expected.Size ||
            !string.Equals(current.FinalPath, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The interrupted local deletion object differs from the immutable source.");
        current.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  current.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The interrupted local deletion bytes differ from the immutable source.");
        root.VerifyBinding();
        return identity;
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
