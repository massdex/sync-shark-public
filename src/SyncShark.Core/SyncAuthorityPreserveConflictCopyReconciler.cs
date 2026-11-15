using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityPreserveConflictCopyReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.ResolveConflict || operation.PairId != pair.Id ||
            operation.Status != SyncAuthorityOperationStatus.EffectUnknown)
            throw new InvalidOperationException("The requested operation is not an unknown visible conflict-copy preservation.");
        var attempt = operation.Attempts.Single(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown);
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The conflict-copy source version is absent."), cancellationToken);
        var destinationPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey
            ?? throw new InvalidDataException("The conflict-copy destination path is absent."));
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var matches = (await remoteStore.ListEntriesAsync(cancellationToken)).Where(item =>
                string.Equals(item.RelativePath, destinationPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            if (matches.Length == 0)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                    "{\"reconciliation\":\"visible conflict-copy destination absent\"}",
                    Hash("preserve-reconciled-absent", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"visible conflict-copy absence verified\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready, false,
                    "The unknown copy is verified not applied and can be retried under a new fenced attempt.");
            }
            if (matches.Length != 1 || matches[0].Kind != PilotEntryKind.File || matches[0].Id is null ||
                matches[0].Revision is null || sourceVersion.ContentHash is null || sourceVersion.Size is null ||
                matches[0].Size != sourceVersion.Size || !string.Equals(matches[0].ContentHash,
                    sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                var observed = JsonSerializer.Serialize(matches.Select(CurrentState));
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", observed,
                    "{\"reconciliation\":\"visible conflict-copy destination collision\"}",
                    Hash("preserve-reconciled-conflict", attempt.RequestFingerprint, observed), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"visible conflict-copy destination differs\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                    "The deterministic copy destination is occupied by a different object.");
            }

            var copied = matches[0];
            var destinationJson = JsonSerializer.Serialize(CurrentState(copied));
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", destinationJson,
                "{\"reconciliation\":\"expected visible conflict copy present\"}",
                Hash("preserve-reconciled-success", attempt.RequestFingerprint, destinationJson), cancellationToken);
            await new SyncAuthorityPreserveConflictCopyExecutor(pair, remoteStore, authority)
                .CommitCopiedObjectAsync(operation, destinationMember, attempt.AttemptId, lease, destinationPath,
                    copied.Id!, copied.Revision!, copied.Size, copied.ContentHash!, cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"visible conflict copy reconciled and completed\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The exact visible copy was committed from read-only reconciliation without replay.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private static object CurrentState(PilotRemoteEntry entry) => new
    {
        entry.RelativePath,
        entry.Kind,
        entry.Id,
        entry.Revision,
        entry.Size,
        entry.ContentHash
    };

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
