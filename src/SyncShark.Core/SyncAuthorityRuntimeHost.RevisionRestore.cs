using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    // Historical bytes differ by provider; admission, predecessor retention, conditional
    // replacement and durable receipts do not. Reuse the manual cloud-restore journal.
    private async Task<string> RestoreJournalledRevisionAsync(SyncPair pair,
        SyncRecoveryCatalogItem requested, string destination, bool restoreAsCopy, string commandId,
        ISyncRemoteStore remote,
        Func<Func<Stream, string, Task<string>>, Func<Func<Task>, Task>, CancellationToken, Task<string>> readBytes,
        Func<bool> privateReadCleanupVerified,
        CancellationToken cancellationToken)
    {
        if (SyncCloudPairConfiguration.IsCloudOnly(pair))
            throw new NotSupportedException("Select an explicitly bound endpoint recovery item for a cloud-to-cloud pair.");
        if (requested.Size is null or < 0 || string.IsNullOrWhiteSpace(requested.Identity) ||
            string.IsNullOrWhiteSpace(requested.Revision) || string.IsNullOrWhiteSpace(requested.ContentHash))
            throw new InvalidDataException("The selected revision has incomplete retained identity, size or checksum.");
        destination = PilotPath.NormalizeRelative(destination);
        var recoveryRoot = SyncDropboxArtifactPaths.RequireBoundRecoveryContainer(pair);
        await remote.VerifyRecoveryRootAsync(recoveryRoot, cancellationToken);
        var current = await remote.FindEntryAtPathAsync(destination, cancellationToken);
        RequireRevisionDestination(current, restoreAsCopy);
        if (!restoreAsCopy && current is not null && current.Id == requested.Identity &&
            current.Revision == requested.Revision)
            return "The selected revision is already current; no provider effect was issued.";

        using var local = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var installation = await EnsureAuthorityInitializedAsync(cancellationToken);
        PairAuthorityRegistration registration;
        if (IsGooglePair(pair))
        {
            await RequirePairAccountAsync(pair, pair.ConnectionId, cancellationToken);
            var binding = _googleDriveConnections.Load(pair.ConnectionId)
                ?? throw new InvalidOperationException("The retained Google account is unavailable.");
            registration = await RegisterGooglePairAuthorityAsync(pair, binding, installation, local, cancellationToken);
        }
        else
            registration = await RegisterPairAuthorityAsync(pair,
                await RequireConnectionAsync(cancellationToken, pair.ConnectionId), installation, local, cancellationToken);
        var admission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        var idempotencyKey = Id("idempotency", Id("operation", commandId, "revision-restore", destination));
        var actionRegistration = await _authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
        var operationId = actionRegistration.OperationId;
        var objectId = Id("object", operationId);
        var options = new SyncAuthorityPlannerOptions(pair.PolicyRevision, registration.ProfileId,
            pair.ControlNamespace?.NamespaceId ?? throw new IOException("The retained control namespace is absent."),
            registration.DropboxRecoveryAuthorityId);
        var allocations = current is null ? Array.Empty<SyncAuthorityAllocation>() :
            SyncAuthorityOperationAllocations.Build(operationId, SyncAuthorityOperationKind.UpdateRemote,
                destination, registration.LocalRootBindingId, registration.DropboxRootBindingId, "file",
                requested.Size, options).Where(row => row.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).ToArray();
        var preconditions = JsonSerializer.Serialize(new
        {
            action = "restore_recovery", restoreKind = "cloud-recovery-restore", pairId = pair.Id,
            path = destination, configurationRevision = pair.PolicyRevision, restoreCommandId = commandId,
            recoveryArtifactId = requested.Key, endpointRootBindingId = registration.DropboxRootBindingId,
            revisionRestore = true, requested.Identity, requested.Revision, requested.Size, requested.ContentHash,
            restoreAsCopy, destinationAbsent = current is null, destination = current
        });
        await _authority.RegisterObjectAsync(objectId, "file", cancellationToken);
        await _authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
            idempotencyKey, SyncAuthorityOperationKind.Reconcile, pair.Id, objectId,
            pair.PolicyRevision, registration.ProfileId, preconditions, 100,
            [new SyncAuthorityMember(Id("member", operationId, "affected"), pair.Id, objectId,
                "affected", registration.DropboxRootBindingId, null, null, null, destination, preconditions)],
            Array.Empty<SyncAuthorityDependency>(), allocations), admission, cancellationToken,
            reservation: actionRegistration.Reservation);
        await using var renewal = await _authority.BeginFencedOperationLeaseRenewalAsync(operationId,
            commandId, SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = renewal.Lease;
        var token = renewal.WorkToken;
        SyncAuthorityAttempt? pending = null;
        var dispatched = false;
        try
        {
            return await readBytes(async (bytes, digest) =>
            {
                if (requested.Size is null || bytes.Length != requested.Size || string.IsNullOrWhiteSpace(digest))
                    throw new IOException("The selected revision's verified byte length or checksum is absent.");
                await RebindAsync();
                var allocation = allocations.SingleOrDefault();
                if (current is not null)
                {
                    await _authority.TransitionAllocationAsync(allocation!.AllocationId, lease,
                        SyncAuthorityAllocationStatus.Creating, cancellationToken: token);
                    await BeginAsync("revision-predecessor-copy", current.Revision!);
                    var retained = await remote.CopyToRecoveryAsync(current, recoveryRoot,
                        allocation.DeterministicName, token);
                    var readback = await remote.GetRecoveryAsync(recoveryRoot, allocation.DeterministicName, token);
                    if (readback != retained || retained.Size != current.Size ||
                        !string.Equals(retained.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The exact destination predecessor was not independently verified in Recovery.");
                    var recoveryJson = JsonSerializer.Serialize(new
                    {
                        retained.FileId, retained.Revision, retained.RecoveryPath, retained.Size,
                        retained.ContentHash, originalPath = destination, kind = "file"
                    });
                    await SuccessAsync(recoveryJson);
                    foreach (var status in new[] { SyncAuthorityAllocationStatus.Created,
                                 SyncAuthorityAllocationStatus.Bound, SyncAuthorityAllocationStatus.Verified })
                        await _authority.TransitionAllocationAsync(allocation.AllocationId, lease, status,
                            retained.FileId, retained.ContentHash, retained.Size, token);
                    await _authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operationId), operationId,
                        objectId, allocation.AllocationId, "dropbox", recoveryJson,
                        retained.ContentHash, retained.Size, token);
                    await IntermediateAsync();
                }
                // The server's revision condition remains decisive even if a collaborator
                // changes the file between this fresh read and the request.
                await RebindAsync();
                await BeginAsync("revision-conditional-upload", digest);
                bytes.Position = 0;
                var uploaded = await remote.UploadAsync(destination, bytes, digest, current?.Revision, token);
                var successor = await remote.FindEntryAtPathAsync(destination, token);
                RequireRevisionDestination(successor, false);
                if (successor is null || successor.Id != uploaded.FileId || successor.Revision != uploaded.Revision ||
                    successor.Size != requested.Size || uploaded.Size != requested.Size ||
                    !string.Equals(successor.ContentHash, digest, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(uploaded.ContentHash, digest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The conditional restore receipt and independent destination readback disagree.");
                var destinationJson = JsonSerializer.Serialize(new
                    { path = destination, successor.Id, successor.Revision, successor.Size, successor.ContentHash });
                await SuccessAsync(destinationJson);
                await _authority.CommitSuccessfulManualRestoreAttemptAsync(pending!.AttemptId, lease, commandId,
                    requested.Key, destination, successor.Id!, successor.Revision!, successor.Size,
                    successor.ContentHash!, destinationJson, token);
                pending = null;
                await _authority.CompleteOperationAsync(lease, destinationJson, token);
                return current is null
                    ? "The exact verified revision was restored as a new file."
                    : "The exact verified revision was restored conditionally; the previous file is retained in Recovery.";
            }, async retainRevision =>
            {
                await RebindAsync();
                await BeginAsync("revision-retention", requested.Revision!);
                await retainRevision();
                await SuccessAsync(JsonSerializer.Serialize(new { requested.Identity, requested.Revision, retained = true }));
                await IntermediateAsync();
            }, token);
        }
        catch (Exception error)
        {
            // Cancellation and lost/malformed replies are NOT evidence of non-application.
            // Keep request_sent if even the bounded journal write cannot be established.
            using var recordWindow = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                if (pending is not null)
                    await _authority.RecordReceiptAsync(pending.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
                        preconditions, "{}", JsonSerializer.Serialize(new { classification = error.GetType().Name }),
                        SyncAuthorityCloudExecution.Hash("revision-restore-unknown", operationId, pending.AttemptId), recordWindow.Token);
                else if (!dispatched && privateReadCleanupVerified())
                    await _authority.SupersedeNoEffectManualRestoreAsync(operationId, lease, commandId,
                        requested.Key, destination, true, false, false, 0,
                        JsonSerializer.Serialize(new { result = "revision read refused before any provider effect" }),
                        recordWindow.Token);
            }
            catch { /* Retain the original failure and durable unresolved operation. */ }
            throw;
        }
        finally
        {
            try { await renewal.ReleaseAsync(CancellationToken.None); }
            catch when (dispatched) { /* An unresolved effect is never replayed to repair lease bookkeeping. */ }
        }

        async Task RebindAsync()
        {
            local.VerifyBinding();
            var retained = (await _authority.LoadPairSetAsync(token)).Pairs.SingleOrDefault(row => row.Id == pair.Id);
            if (retained is null || retained.PolicyRevision != pair.PolicyRevision || !SameRootIdentity(retained, pair))
                throw new IOException("The pair's reviewed root or policy changed. No new restore request was issued.");
            await RequirePairAccountAsync(pair, pair.ConnectionId, token);
            await remote.VerifyRecoveryRootAsync(recoveryRoot, token);
            var observed = await remote.FindEntryAtPathAsync(destination, token);
            RequireRevisionDestination(observed, restoreAsCopy);
            if (!SameRevisionDestination(observed, current))
                throw new IOException("The destination changed during revision restore; the newer file was not overwritten.");
        }
        async Task BeginAsync(string effect, string fingerprint)
        {
            pending = await _authority.StartDispatchedAttemptAsync(operationId, lease,
                SyncAuthorityCloudExecution.Hash("revision-restore-request", operationId, effect, fingerprint), effect, token);
            dispatched = true;
        }
        Task SuccessAsync(string destinationJson) => _authority.RecordReceiptAsync(pending!.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess, preconditions, destinationJson, "{}",
            SyncAuthorityCloudExecution.Hash("revision-restore-success", operationId, pending.AttemptId, destinationJson), token);
        async Task IntermediateAsync()
        {
            await _authority.CompleteIntermediateAttemptAsync(pending!.AttemptId, lease, "{}", token);
            pending = null;
        }
    }

    private static bool SameRevisionDestination(PilotRemoteEntry? observed, PilotRemoteEntry? expected) =>
        observed is null ? expected is null : expected is not null && observed.Kind == expected.Kind &&
        observed.Id == expected.Id && observed.Revision == expected.Revision && observed.Size == expected.Size &&
        string.Equals(observed.RelativePath, expected.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(observed.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static void RequireRevisionDestination(PilotRemoteEntry? entry, bool restoreAsCopy)
    {
        if (entry is null) return;
        if (restoreAsCopy) throw new IOException("The recovered-copy destination is occupied. Choose a fresh copy name.");
        if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null || string.IsNullOrWhiteSpace(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.Revision) || string.IsNullOrWhiteSpace(entry.ContentHash) || entry.Size < 0)
            throw new IOException("The destination has no complete, transferable file identity; it was not replaced.");
    }
}
