using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityMoveLocalReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.MoveLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted local move.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The local move has no reconcilable attempt.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var remoteVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId!, cancellationToken);
        var localVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId!, cancellationToken);
        var desiredPath = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey!);
        var priorPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey!);
        if (localVersion.ContentHash is null)
        {
            if (attempt.EffectKind != "local-directory-identity-move")
                throw new InvalidDataException("The interrupted effect is not a local directory identity move.");
            return await ReconcileDirectoryAsync(operation, attempt, sourceMember, destinationMember,
                remoteVersion, localVersion, desiredPath, priorPath, workerId, cancellationToken);
        }
        if (attempt.EffectKind != "local-identity-move")
            throw new InvalidDataException("The interrupted effect is not a local identity move.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var desiredFullPath = PilotPath.ResolveContainedPath(root.Path, desiredPath);
            var priorFullPath = PilotPath.ResolveContainedPath(root.Path, priorPath);
            var current = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken, contentProvider: pair.Provider, expectedPairId: pair.Id);
            var desiredPresent = current.Entries.Values.Any(item => string.Equals(item.RelativePath,
                desiredPath, StringComparison.Ordinal));
            var priorPresent = current.Entries.Values.Any(item => string.Equals(item.RelativePath,
                priorPath, StringComparison.Ordinal));
            if (desiredPresent && !priorPresent)
            {
                await using var moved = SyncLocalFileAuthority.OpenExistingExclusive(desiredFullPath);
                var identity = await SyncAuthorityMoveLocalExecutor.VerifyExactLocalAsync(pair.Provider, moved, desiredFullPath,
                    localVersion, root, cancellationToken);
                await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
                var proof = JsonSerializer.Serialize(new { priorPath, desiredPath,
                    identity = SyncAuthorityMoveLocalExecutor.Format(identity), result = "move applied" });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess,
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion), proof, proof,
                        SyncAuthorityMoveLocalExecutor.Hash("move-local-reconciled", proof), cancellationToken);
                var generation = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                    desiredPath, cancellationToken);
                var membershipId = SyncAuthorityMoveLocalExecutor.Id("membership-local-move", operationId,
                    desiredPath, localVersion.ObjectVersionId);
                await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
                    destinationMember.RootBindingId, desiredPath, generation, cancellationToken: cancellationToken);
                // Same point as the executor: the Dropbox baseline version the moved file still matches.
                var pointRemoteVersion = await authority.LoadMoveBaselineVersionAsync(operation, "dropbox",
                    cancellationToken);
                var pointId = SyncAuthorityMoveLocalExecutor.Id("syncpoint-move", operationId,
                    localVersion.ObjectVersionId, pointRemoteVersion, desiredPath);
                if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
                    throw new InvalidOperationException("Local identity moves do not emit provider-unknown receipts.");
                await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                    operation.ObjectId!, localVersion.ObjectVersionId, pointRemoteVersion, membershipId,
                    sourceMember.MembershipId, proof, cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"local move reconciled\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the exact NTFS identity at the intended membership.");
            }
            if (priorPresent && !desiredPresent)
            {
                await using var prior = SyncLocalFileAuthority.OpenExistingExclusive(priorFullPath);
                await SyncAuthorityMoveLocalExecutor.VerifyExactLocalAsync(pair.Provider, prior, priorFullPath, localVersion, root,
                    cancellationToken);
                var proof = "{\"result\":\"local move not applied\"}";
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied,
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion),
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion), proof,
                        SyncAuthorityMoveLocalExecutor.Hash("move-local-not-applied", proof), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready, false,
                    "The local move was not applied; one fenced retry is allowed.");
            }
            var blocked = "{\"result\":\"local move membership state is ambiguous\"}";
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", blocked,
                    SyncAuthorityMoveLocalExecutor.Hash("move-local-ambiguous", blocked), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, blocked, cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                "The local move membership state is ambiguous; no retry is allowed.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityAttempt attempt,
        SyncAuthorityMember sourceMember, SyncAuthorityMember destinationMember,
        SyncAuthorityObjectVersion remoteVersion, SyncAuthorityObjectVersion localVersion,
        string desiredPath, string priorPath, string workerId, CancellationToken cancellationToken)
    {
        var expectedIdentity = SyncAuthorityMoveLocalExecutor.LocalDirectoryIdentity(localVersion);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var desiredFullPath = PilotPath.ResolveContainedPath(root.Path, desiredPath);
            var priorFullPath = PilotPath.ResolveContainedPath(root.Path, priorPath);
            var current = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken, contentProvider: pair.Provider, expectedPairId: pair.Id);
            var desiredPresent = current.Entries.Values.Any(item => string.Equals(item.RelativePath,
                desiredPath, StringComparison.Ordinal));
            var priorPresent = current.Entries.Values.Any(item => string.Equals(item.RelativePath,
                priorPath, StringComparison.Ordinal));
            if (desiredPresent && !priorPresent && Directory.Exists(desiredFullPath))
            {
                using var moved = PilotDirectoryLease.OpenOwnedDirectory(desiredFullPath, expectedIdentity);
                moved.VerifyBinding();
                await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
                var proof = JsonSerializer.Serialize(new
                {
                    priorPath,
                    desiredPath,
                    kind = "directory",
                    identity = SyncAuthorityMoveLocalExecutor.Format(expectedIdentity),
                    result = "move applied"
                });
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownSuccess,
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion), proof, proof,
                        SyncAuthorityMoveLocalExecutor.Hash("move-local-directory-reconciled", proof),
                        cancellationToken);
                var generation = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                    desiredPath, cancellationToken);
                var membershipId = SyncAuthorityMoveLocalExecutor.Id("membership-local-move",
                    operation.OperationId, desiredPath, localVersion.ObjectVersionId);
                await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
                    destinationMember.RootBindingId, desiredPath, generation,
                    cancellationToken: cancellationToken);
                var pointId = SyncAuthorityMoveLocalExecutor.Id("syncpoint-move", operation.OperationId,
                    localVersion.ObjectVersionId, remoteVersion.ObjectVersionId, desiredPath);
                await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                    operation.ObjectId!, localVersion.ObjectVersionId, remoteVersion.ObjectVersionId,
                    membershipId, sourceMember.MembershipId, proof, cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"local directory move reconciled\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Completed, false,
                    "Read-only reconciliation proved the exact NTFS directory at its intended membership.");
            }
            if (priorPresent && !desiredPresent && Directory.Exists(priorFullPath))
            {
                using var prior = PilotDirectoryLease.OpenOwnedDirectory(priorFullPath, expectedIdentity);
                prior.VerifyBinding();
                var proof = "{\"result\":\"local directory move not applied\"}";
                if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied,
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion),
                        SyncAuthorityMoveLocalExecutor.LocalVersionJson(localVersion), proof,
                        SyncAuthorityMoveLocalExecutor.Hash("move-local-directory-not-applied", proof),
                        cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied, proof, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The local directory move was not applied; one fenced retry is allowed.");
            }
            var blocked = "{\"result\":\"local directory move membership state is ambiguous\"}";
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}", blocked,
                    SyncAuthorityMoveLocalExecutor.Hash("move-local-directory-ambiguous", blocked),
                    cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, blocked, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Blocked, false,
                "The local directory move state is ambiguous; no retry is allowed.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private async Task RequireExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var id = identity.RootElement.GetProperty("fileId").GetString();
        var revision = identity.RootElement.GetProperty("revision").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var matches = (await remoteStore.ListEntriesAsync(cancellationToken)).Where(item =>
            string.Equals(item.RelativePath, path, StringComparison.Ordinal)).ToArray();
        var exact = matches.Length == 1 && string.Equals(matches[0].Id, id, StringComparison.Ordinal) &&
                    (expected.ContentHash is null
                        ? matches[0].Kind == PilotEntryKind.Directory && expected.Size is 0
                        : matches[0].Kind == PilotEntryKind.File && expected.Size is not null &&
                          string.Equals(matches[0].Revision, revision, StringComparison.Ordinal) &&
                          matches[0].Size == expected.Size && string.Equals(matches[0].ContentHash,
                              expected.ContentHash, StringComparison.OrdinalIgnoreCase));
        if (!exact)
            throw new IOException("The Dropbox side of the local move is no longer exact.");
    }
}
