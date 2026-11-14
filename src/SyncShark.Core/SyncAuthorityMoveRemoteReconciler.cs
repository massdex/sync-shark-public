using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityMoveRemoteReconciler(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    public async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.MoveRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new InvalidOperationException("The requested operation is not an interrupted remote move.");
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            ?? throw new InvalidOperationException("The remote move has no reconcilable attempt.");
        if (attempt.EffectKind != "dropbox-native-move")
            throw new InvalidDataException("The interrupted operation is not an identity-preserving Dropbox move.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var localVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId!, cancellationToken);
        var predecessorVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId!,
            cancellationToken);
        var desiredPath = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey!);
        var priorPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey!);
        using var predecessorIdentity = JsonDocument.Parse(predecessorVersion.IdentityJson);
        var predecessorId = predecessorIdentity.RootElement.GetProperty("fileId").GetString()
                            ?? throw new InvalidDataException("The remote predecessor identity is absent.");

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            // SS-D18: a dispatched request that retained no durable receipt has no proof of its outcome.
            // Recording a receipt always moves its attempt out of request_sent (to response_received or
            // effect_unknown), so a request_sent attempt is exactly the crash boundary where the request was
            // sent and no receipt exists. The current local source and the current remote path are not
            // evidence about that sent request, so the operation is returned explicitly unresolved with no
            // outcome, receipt, attempt transition or retry derived from a fresh scan or listing. A durable
            // terminal receipt (known success or known not-applied) is the only retained outcome proof this
            // reconciler accepts; the renewal lease is released normally either way.
            var retainedReceipt = await authority.LoadLatestEffectReceiptAsync(operationId, attempt.AttemptId,
                cancellationToken);
            var durableTerminalReceipt = retainedReceipt is not null &&
                (string.Equals(retainedReceipt.Certainty,
                     SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownSuccess),
                     StringComparison.Ordinal) ||
                 string.Equals(retainedReceipt.Certainty,
                     SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownNotApplied),
                     StringComparison.Ordinal));
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent && !durableTerminalReceipt)
                return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                    "The Dropbox move request was sent without a durable receipt, so its outcome is unknown; " +
                    "the operation stays explicitly unresolved and no outcome or retry is derived from the " +
                    "current local source or the current remote path.");

            if (retainedReceipt is not null && retainedReceipt.Certainty == "known_success")
            {
                using var raw = JsonDocument.Parse(retainedReceipt.DestinationIdentityJson);
                var value = raw.RootElement;
                if (value.TryGetProperty("classification", out var classification) &&
                    classification.GetString() == SyncAuthorityMoveRemoteExecutor.SuccessorIdentityConflict)
                {
                    using var source = JsonDocument.Parse(retainedReceipt.SourceIdentityJson);
                    var expectedKind = predecessorVersion.ContentHash is null
                        ? PilotEntryKind.Directory : PilotEntryKind.File;
                    if (source.RootElement.GetProperty("Id").GetString() != predecessorId ||
                        value.GetProperty("Id").GetString() != predecessorId ||
                        value.GetProperty("RelativePath").GetString() != desiredPath ||
                        value.GetProperty("Kind").GetInt32() != (int)expectedKind ||
                        value.TryGetProperty("ObjectVersionId", out _) ||
                        value.TryGetProperty("MembershipId", out _))
                        throw new InvalidDataException("The retained applied-move conflict receipt has foreign authority.");
                    // This exact attempt already proved the move. Settle its interrupted blocked
                    // transition before observing current paths, which cannot revise that outcome.
                    await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                        SyncAuthorityAttemptStatus.Blocked, retainedReceipt.DestinationIdentityJson,
                        cancellationToken);
                    return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked,
                        false, "Dropbox moved the file and its latest contents; retained version identity conflicts, " +
                        "so synchronization needs a fresh comparison.");
                }
            }

            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            // The same exact scoped root proof the executor uses: the exact desired path only, without
            // traversing a directory source or enumerating siblings and descendants, while a file source keeps
            // its exact held-bytes proof.
            var localScan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                includePath: item => string.Equals(PilotPath.CanonicalComparisonKey(item),
                    PilotPath.CanonicalComparisonKey(desiredPath), StringComparison.Ordinal),
                traverseDirectory: _ => false, refreshPaths: [desiredPath],
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!localScan.Entries.TryGetValue(desiredPath, out var local) ||
                !string.Equals(local.RelativePath, desiredPath, StringComparison.Ordinal))
                return await BlockAsync(operationId, attempt, lease,
                    "{\"result\":\"local moved identity is absent during reconciliation\"}", cancellationToken);
            SyncAuthorityMoveRemoteExecutor.RequireExactLocal(local, localVersion);

            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var entries = await remoteStore.ListEntriesAsync(cancellationToken);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var identityMatches = entries.Where(item => string.Equals(item.Id, predecessorId,
                StringComparison.Ordinal)).ToArray();
            var targetOccupants = entries.Where(item => string.Equals(item.RelativePath, desiredPath,
                StringComparison.Ordinal)).ToArray();

            var knownSuccessReceipt = retainedReceipt is not null && string.Equals(retainedReceipt.Certainty,
                SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownSuccess),
                StringComparison.Ordinal);
            var atExactDestination = identityMatches.Length == 1 && targetOccupants.Length == 1 &&
                                      ReferenceEquals(identityMatches[0], targetOccupants[0]);
            // SS-D17: a retained known-success receipt with the exact stable identity and kind at the exact
            // destination, but contents that differ from the retained predecessor, is the same known-move /
            // content-comparison-pending state the executor records. It is settled as that held state: no
            // equal-content baseline is invented, no synchronization point is committed and no second network
            // move is unlocked from a current path observation. The durable receipt stays the single retained
            // successful-effect evidence; only the fenced blocked classification is recorded.
            if (atExactDestination && knownSuccessReceipt &&
                RemoteIdentityKindMatches(identityMatches[0], predecessorVersion) &&
                !MatchesVersion(identityMatches[0], predecessorVersion))
            {
                var pendingJson = JsonSerializer.Serialize(new
                {
                    classification = "native_move_applied_content_comparison_pending",
                    fileId = identityMatches[0].Id,
                    path = identityMatches[0].RelativePath,
                    kind = identityMatches[0].Kind.ToString(),
                    revision = identityMatches[0].Revision,
                    size = identityMatches[0].Size,
                    contentHash = identityMatches[0].ContentHash
                });
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked, pendingJson, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
                    "Dropbox moved the file and its latest contents; synchronization needs a fresh comparison.");
            }

            if (atExactDestination && knownSuccessReceipt && MatchesVersion(identityMatches[0], predecessorVersion))
            {
                var moved = identityMatches[0];
                var executor = new SyncAuthorityMoveRemoteExecutor(pair, remoteStore, authority);
                var successor = await executor.RegisterSuccessorAsync(operation,
                    destinationMember.RootBindingId, desiredPath, moved, cancellationToken);
                // Same point as the executor: the local baseline version the moved object still matches.
                var pointLocalVersion = await authority.LoadMoveBaselineVersionAsync(operation, "local",
                    cancellationToken);
                var pointId = SyncAuthorityMoveRemoteExecutor.Id("syncpoint-move", operationId,
                    pointLocalVersion, successor.ObjectVersionId, desiredPath);
                if (attempt.Status == SyncAuthorityAttemptStatus.EffectUnknown)
                    await authority.CommitReconciledSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId,
                        pair.Id, operation.ObjectId!, pointLocalVersion,
                        successor.ObjectVersionId, sourceMember.MembershipId, successor.MembershipId,
                        "{\"result\":\"native remote move reconciled by stable identity\"}", cancellationToken);
                else
                    await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                        operation.ObjectId!, pointLocalVersion, successor.ObjectVersionId,
                        sourceMember.MembershipId, successor.MembershipId,
                        "{\"result\":\"native remote move reconciled by stable identity\"}", cancellationToken);
                await authority.CompleteOperationAsync(lease,
                    "{\"result\":\"identity-preserving remote move reconciliation complete\"}",
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed,
                    false, "The existing Dropbox object is verified at its moved destination.");
            }

            var prior = identityMatches.Length == 1 &&
                        string.Equals(identityMatches[0].RelativePath, priorPath,
                            StringComparison.Ordinal) &&
                        MatchesVersion(identityMatches[0], predecessorVersion);
            if (prior && targetOccupants.Length == 0)
            {
                // SS-D18: an identity still at its prior path with a free destination is an observation, not
                // terminal proof that the sent request was never applied. Without the provider's own retained
                // known-not-applied receipt, that observation cannot reclassify the attempt, cannot clear the
                // reservation and cannot open a retry: the exact attempt, receipt and operation state stay
                // unresolved so a delayed original completion can never be duplicated by a reissue.
                var definitiveNotApplied = retainedReceipt is not null && string.Equals(
                    retainedReceipt.Certainty,
                    SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownNotApplied),
                    StringComparison.Ordinal);
                if (!definitiveNotApplied)
                    return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                        "The interrupted Dropbox move has no durable terminal receipt, so the current remote " +
                        "path cannot prove non-application; the operation stays explicitly unresolved and no " +
                        "retry is opened.");
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"native move verified not applied\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Ready,
                    false, "The Dropbox identity remains at the source and one fenced retry is allowed.");
            }

            return await BlockAsync(operationId, attempt, lease,
                "{\"result\":\"Dropbox move identity or destination is ambiguous\"}", cancellationToken);
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private static bool MatchesVersion(PilotRemoteEntry current, SyncAuthorityObjectVersion expected) =>
        expected.ContentHash is null
            ? current.Kind == PilotEntryKind.Directory && expected.Size is 0
            : current.Kind == PilotEntryKind.File && current.Size == expected.Size &&
              string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase) &&
              RevisionMatches(current, expected);

    private static bool RevisionMatches(PilotRemoteEntry current, SyncAuthorityObjectVersion expected)
    {
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        return string.Equals(current.Revision,
            identity.RootElement.GetProperty("revision").GetString(), StringComparison.Ordinal);
    }

    // The immutable part of a moved membership: the same kind the retained member declared. Content is
    // deliberately excluded so a moved object is still recognised after its remote contents changed.
    private static bool RemoteIdentityKindMatches(PilotRemoteEntry current,
        SyncAuthorityObjectVersion expected) =>
        expected.ContentHash is null
            ? current.Kind == PilotEntryKind.Directory
            : current.Kind == PilotEntryKind.File;

    private async Task<SyncAuthorityExecutionResult> BlockAsync(string operationId,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, string detail,
        CancellationToken cancellationToken)
    {
        // A request_sent attempt is returned unresolved before any listing branch, so this refusal only ever
        // reclassifies an attempt that already holds its own durable receipt. It records no new receipt and
        // never derives known-absence or known-success from the observed paths.
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.Blocked, detail, cancellationToken);
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false,
            "The interrupted Dropbox move is ambiguous; all observed content was left unchanged.");
    }

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
