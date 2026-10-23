using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityPreserveConflictCopyExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.ResolveConflict || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new InvalidOperationException("The requested operation is not a runnable visible conflict-copy preservation.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        _ = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The conflict-copy source has no exact version authority."), cancellationToken);
        var sourcePath = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The conflict-copy source path is absent."));
        var destinationPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey
            ?? throw new InvalidDataException("The conflict-copy destination path is absent."));
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The conflict-copy destination is not distinct from its source.");

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            var source = await new SyncAuthorityUpdateRemoteExecutor(pair, remoteStore, authority)
                .LoadExactRemoteAsync(sourcePath, sourceVersion, cancellationToken);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var destinationMatches = (await remoteStore.ListEntriesAsync(cancellationToken)).Where(item =>
                string.Equals(item.RelativePath, destinationPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (destinationMatches.Length != 0)
                return await BlockWithoutDispatchAsync(operationId, lease,
                    "The deterministic visible conflict-copy destination already exists.", cancellationToken);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);

            var requestFingerprint = Hash("preserve-conflict-copy", pair.Id, source.Id!, source.Revision!,
                sourcePath, destinationPath, sourceVersion.ObjectVersionId, sourceVersion.ContentHash!,
                sourceVersion.Size!.Value.ToString(CultureInfo.InvariantCulture));
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, requestFingerprint,
                "dropbox-visible-conflict-copy-add", cancellationToken);
            dispatched = true;
            PilotUploadResult copied;
            try
            {
                copied = await remoteStore.CopyCreateOnlyAsync(source, destinationPath, cancellationToken);
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(source), "{}",
                    JsonSerializer.Serialize(new { classification = "provider_rejected", exception.StatusCode }),
                    Hash("preserve-not-applied", requestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"visible conflict-copy precondition rejected\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, true,
                    "Dropbox rejected the exact create-only conflict copy without applying it.");
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, RemoteJson(source), "{}",
                    "{\"classification\":\"visible conflict-copy response unknown\"}",
                    Hash("preserve-unknown", requestFingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true, "The visible conflict-copy response is unknown and must be reconciled before retry.");
            }

            if (copied.Size != sourceVersion.Size ||
                !string.Equals(copied.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The visible conflict copy differs from the immutable Dropbox version.");
            var destinationJson = JsonSerializer.Serialize(new
            {
                path = destinationPath,
                copied.FileId,
                copied.Revision,
                copied.Size,
                copied.ContentHash
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(source), destinationJson, destinationJson,
                Hash("preserve-success", requestFingerprint, destinationJson), cancellationToken);
            await CommitCopiedObjectAsync(operation, destinationMember, attempt.AttemptId, lease, destinationPath,
                copied.FileId, copied.Revision, copied.Size, copied.ContentHash, cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"visible Dropbox conflict copy preserved\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact Dropbox conflict version was preserved under its deterministic visible copy name.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    internal async Task CommitCopiedObjectAsync(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityMember destinationMember, string attemptId, SyncAuthorityLease lease, string destinationPath,
        string fileId, string revision, long size, string contentHash, CancellationToken cancellationToken)
    {
        var objectId = operation.ObjectId
            ?? throw new InvalidDataException("The conflict-copy operation has no destination object identity.");
        if (!string.Equals(destinationMember.ObjectId, objectId, StringComparison.Ordinal))
            throw new InvalidDataException("The conflict-copy destination member belongs to another object.");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var identity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot, fileId, revision);
        var versionId = Id("version-dropbox", objectId, identity, contentHash,
            size.ToString(CultureInfo.InvariantCulture));
        var generation = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
            destinationPath, cancellationToken);
        var membershipId = Id("membership-dropbox-effect", operation.OperationId, destinationPath, versionId);
        await authority.RegisterObjectAsync(objectId, "file", cancellationToken);
        await authority.RegisterObjectVersionAsync(versionId, objectId, "dropbox", identity, contentHash, size,
            cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, objectId, destinationMember.RootBindingId,
            destinationPath, generation, cancellationToken: cancellationToken);
        await authority.CommitSuccessfulAuxiliaryAttemptAsync(attemptId, lease,
            "{\"result\":\"visible conflict copy registered without claiming cross-side convergence\"}",
            cancellationToken);
    }

    private async Task<SyncAuthorityExecutionResult> BlockWithoutDispatchAsync(string operationId,
        SyncAuthorityLease lease, string reason, CancellationToken cancellationToken)
    {
        var fingerprint = Hash("preserve-conflict-copy-precondition", operationId, reason);
        var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
            "dropbox-visible-conflict-copy-precondition", cancellationToken);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
            JsonSerializer.Serialize(new { classification = "destination_collision", reason }),
            Hash("preserve-precondition-blocked", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            JsonSerializer.Serialize(new { reason }), cancellationToken);
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false, reason);
    }

    private static string RemoteJson(PilotRemoteEntry entry) => JsonSerializer.Serialize(new
    {
        entry.RelativePath,
        entry.Id,
        entry.Revision,
        entry.Size,
        entry.ContentHash
    });

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
