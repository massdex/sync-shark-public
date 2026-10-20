using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityCommittedOperationFinalizer(
    SyncPair pair,
    SyncAuthorityDatabase authority)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> FinalizeAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.PairId != pair.Id || operation.Status is not
            (SyncAuthorityOperationStatus.Committed or SyncAuthorityOperationStatus.CleanupPending))
            throw new InvalidOperationException("The requested operation has no committed cleanup boundary for this pair.");

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            if (operation.Status == SyncAuthorityOperationStatus.Committed)
                await authority.MarkCleanupPendingAsync(lease,
                    "{\"result\":\"committed operation cleanup started\"}", cancellationToken);

            switch (operation.Kind)
            {
                case SyncAuthorityOperationKind.CreateLocal:
                    await FinalizeCreateLocalAsync(operation, lease, cancellationToken);
                    break;
                case SyncAuthorityOperationKind.UpdateLocal:
                    await FinalizeUpdateLocalAsync(operation, lease, cancellationToken);
                    break;
                case SyncAuthorityOperationKind.DeleteLocal:
                    RequireAllocationShape(operation,
                        SyncAuthorityAllocationKind.TransactionDirectory,
                        SyncAuthorityAllocationKind.LocalRecoveryFile);
                    RequireAllVerified(operation.Allocations);
                    break;
                case SyncAuthorityOperationKind.DeleteRemote:
                case SyncAuthorityOperationKind.MoveRemote:
                    RequireAllocationShape(operation, SyncAuthorityAllocationKind.RemoteRecoveryFile);
                    RequireAllVerified(operation.Allocations);
                    break;
                case SyncAuthorityOperationKind.UpdateRemote:
                    RequireAllocationShapeWithOptionalUploadSession(operation,
                        SyncAuthorityAllocationKind.RemoteRecoveryFile);
                    RequireRemoteRecoveryAndReleasedUploadSession(operation);
                    break;
                case SyncAuthorityOperationKind.CreateRemote:
                    RequireAllocationShapeWithOptionalUploadSession(operation);
                    RequireReleasedUploadSession(operation);
                    break;
                case SyncAuthorityOperationKind.MoveLocal:
                case SyncAuthorityOperationKind.ResolveConflict:
                    RequireAllocationShape(operation);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Committed cleanup is not defined for operation kind {operation.Kind}.");
            }

            PhaseForTest?.Invoke("cleanup-verified-before-completion");
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"committed cleanup verified\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The committed effect and its owned cleanup were recovered without replay.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task FinalizeCreateLocalAsync(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (operation.Attempts.LastOrDefault() is
                { Status: SyncAuthorityAttemptStatus.VerifiedSuccess, EffectKind: "local-create-directory" })
        {
            RequireAllocationShape(operation); // A real directory create has no file stage.
            return;
        }
        if (operation.Attempts.LastOrDefault() is
                { Status: SyncAuthorityAttemptStatus.VerifiedSuccess, EffectKind: "cloud-files-create-placeholder" } &&
            operation.Allocations.All(item => item.Status == SyncAuthorityAllocationStatus.Authorized &&
                item.BoundIdentity is null && item.ContentHash is null))
        {
            using var source = JsonDocument.Parse(operation.Members.Single(item => item.Role == "source").ExpectedStateJson);
            var state = source.RootElement;
            var directory = (state.TryGetProperty("ObjectKind", out var kind) ? kind : state.GetProperty("objectKind")).GetString() == "directory";
            RequireAllocationShape(operation, directory ? [] :
                [SyncAuthorityAllocationKind.TransactionDirectory, SyncAuthorityAllocationKind.StageFile]);
            return; // These branches create no ordinary transaction or stage artifact.
        }
        RequireAllocationShape(operation, SyncAuthorityAllocationKind.TransactionDirectory,
            SyncAuthorityAllocationKind.StageFile);
        var transactionAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        RequireVerifiedOrReleased(stageAllocation);
        RequireVerifiedOrReleased(transactionAllocation);

        var binding = pair.ControlNamespace ??
                      throw new InvalidOperationException("Committed local-create cleanup has no control namespace.");
        using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        using var controlRoot = SyncControlNamespace.OpenLocal(binding, selectedRoot);
        var transactionPath = Path.Combine(controlRoot.Path, transactionAllocation.DeterministicName);

        if (stageAllocation.Status == SyncAuthorityAllocationStatus.Verified)
            await authority.TransitionAllocationAsync(stageAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);

        if (transactionAllocation.Status == SyncAuthorityAllocationStatus.Verified)
        {
            if (Directory.Exists(transactionPath) && !File.Exists(transactionPath))
            {
                if (transactionAllocation.BoundIdentity is null || transactionAllocation.ContentHash is null)
                    throw new InvalidDataException("The committed transaction allocation is not identity-bound.");
                using (var transaction = SyncLocalTransactionDirectory.OpenAuthorized(controlRoot,
                           transactionAllocation.DeterministicName, transactionAllocation.BoundIdentity,
                           transactionAllocation.ContentHash))
                {
                    if (Directory.EnumerateFileSystemEntries(transaction.Path).Any())
                        throw new IOException("Committed local-create cleanup found an unexpected retained artifact.");
                    transaction.DeleteOnClose();
                }
                PhaseForTest?.Invoke("create-local-transaction-removed-before-authority-release");
            }
            else if (File.Exists(transactionPath))
            {
                throw new IOException("The committed transaction allocation became a file.");
            }

            await authority.TransitionAllocationAsync(transactionAllocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
        }

        if (Directory.Exists(transactionPath) || File.Exists(transactionPath))
            throw new IOException("The released local-create transaction still exists.");
    }

    private async Task FinalizeUpdateLocalAsync(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        RequireAllocationShape(operation, SyncAuthorityAllocationKind.TransactionDirectory,
            SyncAuthorityAllocationKind.StageFile, SyncAuthorityAllocationKind.LocalRecoveryFile);
        var transaction = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stage = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recovery = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        RequireVerifiedOrReleased(stage);
        if (transaction.Status != SyncAuthorityAllocationStatus.Verified ||
            recovery.Status != SyncAuthorityAllocationStatus.Verified)
            throw new InvalidOperationException("Committed local replacement recovery is not fully retained.");
        if (stage.Status == SyncAuthorityAllocationStatus.Verified)
            await authority.TransitionAllocationAsync(stage.AllocationId, lease,
                SyncAuthorityAllocationStatus.Released, cancellationToken: cancellationToken);
    }

    private static void RequireAllocationShape(SyncAuthorityOperationSnapshot operation,
        params SyncAuthorityAllocationKind[] expectedKinds)
    {
        var actual = operation.Allocations.Select(item => item.Kind).OrderBy(item => item).ToArray();
        var expected = expectedKinds.OrderBy(item => item).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("The committed operation allocation shape is not recognized.");
    }

    private static void RequireAllVerified(IEnumerable<SyncAuthorityAllocation> allocations)
    {
        if (allocations.Any(item => item.Status != SyncAuthorityAllocationStatus.Verified))
            throw new InvalidOperationException("A committed recovery allocation is not verified.");
    }

    private static void RequireAllocationShapeWithOptionalUploadSession(
        SyncAuthorityOperationSnapshot operation, params SyncAuthorityAllocationKind[] requiredKinds)
    {
        var expected = requiredKinds.ToList();
        if (operation.Allocations.Any(item => item.Kind == SyncAuthorityAllocationKind.UploadSession))
            expected.Add(SyncAuthorityAllocationKind.UploadSession);
        RequireAllocationShape(operation, expected.ToArray());
    }

    private static void RequireRemoteRecoveryAndReleasedUploadSession(
        SyncAuthorityOperationSnapshot operation)
    {
        var recovery = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        if (recovery.Status != SyncAuthorityAllocationStatus.Verified)
            throw new InvalidOperationException("The committed remote predecessor recovery is not verified.");
        RequireReleasedUploadSession(operation);
    }

    private static void RequireReleasedUploadSession(SyncAuthorityOperationSnapshot operation)
    {
        var upload = operation.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.UploadSession);
        if (upload is not null && upload.Status != SyncAuthorityAllocationStatus.Released)
            throw new InvalidOperationException("The committed upload session is not safely released.");
    }

    private static void RequireVerifiedOrReleased(SyncAuthorityAllocation allocation)
    {
        if (allocation.Status is not
            (SyncAuthorityAllocationStatus.Verified or SyncAuthorityAllocationStatus.Released))
            throw new InvalidOperationException("A committed ephemeral allocation is not verified or released.");
    }
}
