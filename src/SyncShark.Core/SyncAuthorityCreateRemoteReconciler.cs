using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityCreateRemoteReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.CreateRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved))
            throw new InvalidOperationException("The requested authority operation is not an interrupted remote create for this pair.");
        var attempt = operation.Attempts.Single(item => item.Status is SyncAuthorityAttemptStatus.RequestSent or
            SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown);
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote-create source has no exact version authority."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote-create source path is absent."));
        var directory = ObjectKind(sourceMember) == "directory";
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            IReadOnlyList<PilotRemoteEntry> entries;
            if (directory) entries = await remoteStore.ListEntriesAsync(cancellationToken);
            else
            {
                var current = await remoteStore.FindEntryAtPathAsync(path, cancellationToken);
                entries = current is null ? [] : [current];
            }
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var matches = entries.Where(item =>
                string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                    "{\"reconciliation\":\"destination absent\"}",
                    Hash("reconciled-not-applied", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"verified destination absence\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready, false,
                    "The unknown add-only upload is verified not applied and may be replanned or retried.");
            }
            var expectedMatch = matches.Length == 1 && matches[0].Id is not null &&
                                (directory
                                    ? matches[0].Kind == PilotEntryKind.Directory &&
                                      sourceVersion.ContentHash is null && sourceVersion.Size is 0
                                    : sourceVersion.ContentHash is not null && sourceVersion.Size is not null &&
                                      matches[0].Kind == PilotEntryKind.File && matches[0].Revision is not null &&
                                      string.Equals(matches[0].ContentHash, sourceVersion.ContentHash,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      matches[0].Size == sourceVersion.Size);
            if (!expectedMatch)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}",
                    JsonSerializer.Serialize(matches.Select(CurrentState)),
                    "{\"reconciliation\":\"destination conflict\"}",
                    Hash("reconciled-conflict", attempt.RequestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"destination differs from expected successor\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                    "The unknown destination is present but does not match the expected successor.");
            }

            var remote = matches[0];
            var uploadAllocation = operation.Allocations.SingleOrDefault(item =>
                item.Kind == SyncAuthorityAllocationKind.UploadSession);
            if (uploadAllocation is not null)
                await new SyncAuthorityUploadSessionCoordinator(authority, uploadAllocation, sourceVersion, lease)
                    .CompleteFromReconciliationAsync(cancellationToken);
            var destinationJson = JsonSerializer.Serialize(CurrentState(remote));
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", destinationJson,
                "{\"reconciliation\":\"expected successor present\"}",
                Hash("reconciled-success", attempt.RequestFingerprint, destinationJson), cancellationToken);
            var remoteIdentity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot,
                remote.Id!, directory ? null : remote.Revision);
            var remoteVersionId = Id("version-dropbox", operation.ObjectId!, remoteIdentity,
                remote.ContentHash ?? "none", remote.Size.ToString(CultureInfo.InvariantCulture));
            var remoteGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var remoteMembershipId = Id("membership-dropbox-effect", operationId, path, remoteVersionId);
            await authority.RegisterObjectVersionAsync(remoteVersionId, operation.ObjectId!, "dropbox",
                remoteIdentity, remote.ContentHash, remote.Size, cancellationToken);
            await authority.RegisterMembershipAsync(remoteMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, remoteGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operationId, sourceVersion.ObjectVersionId, remoteVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, sourceVersion.ObjectVersionId, remoteVersionId, sourceMember.MembershipId,
                remoteMembershipId, "{\"result\":\"unknown remote create reconciled as success\"}",
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"reconciled remote create complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The existing Dropbox successor exactly matches the unknown request and was committed without retry.");
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
