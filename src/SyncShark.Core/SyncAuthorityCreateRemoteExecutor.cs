using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncAuthorityExecutionResult(
    string OperationId,
    SyncAuthorityOperationStatus Status,
    bool EffectDispatched,
    string Reason)
{
    public bool NotAttempted { get; init; }
    // In-memory scheduling hint only. The retained authority independently
    // proves no effect before a reviewed source can be freshly planned.
    internal bool SourceChangedBeforeEffect { get; init; }
    // Destination drift must reclassify an ordinary plan, but must never
    // authorize the reviewed-source-only refresh path to overwrite a new edit.
    internal bool DestinationChangedBeforeEffect { get; init; }
}

internal sealed class SyncAuthoritySourceChangedException(string message) : IOException(message) { }
internal sealed class SyncAuthorityDestinationChangedException(string message) : IOException(message) { }

public sealed class SyncAuthorityCreateRemoteExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.CreateRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new InvalidOperationException("The requested authority operation is not a runnable remote create for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var sourceVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote-create source has no exact version authority."), cancellationToken);
        var path = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The remote-create source path is absent."));
        if (ObjectKind(sourceMember) == "directory")
            return await ExecuteDirectoryAsync(operation, sourceMember, destinationMember, sourceVersion, path,
                retainedRemoteRoot, workerId, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var localScan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                refreshPaths: [path], traverseDirectory: _ => false, contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!localScan.Entries.TryGetValue(path, out var local))
                throw new IOException("The exact local source is no longer present under the selected root.");
            SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(sourceVersion.IdentityJson, pair.Provider);
            RequireExactLocalSource(local, sourceVersion);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            // A small Dropbox create commits through finish_batch_v2 with add mode, no autorename and
            // strict_conflict: it cannot overwrite, and an occupied destination returns a per-entry conflict
            // failure (DropboxApiException -> Blocked below). The per-file absence read is skipped only there.
            var batchedAddOnly = remoteStore is DropboxPilotClient &&
                sourceVersion.Size <= DropboxPilotClient.BatchedUploadMaximumBytes &&
                !SyncUploadLimits.ForStore(remoteStore).RequiresSession(sourceVersion.Size, replacement: false);
            if (!batchedAddOnly)
            {
                await SyncByteProgressStream.ReportProcessAsync("Checking destination", path,
                    "Reading the exact destination metadata for this path to verify it is still absent before an add-only upload; no upload has been sent.");
                if (await remoteStore.FindEntryAtPathAsync(path, cancellationToken) is not null)
                    throw new IOException("The Dropbox destination is no longer absent; the retained create plan was not dispatched.");
                await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            }

            var uploadSession = await PrepareUploadSessionAsync(operation, sourceVersion, lease,
                cancellationToken);

            await using var source = await PilotLocalScanner.OpenValidatedForUploadAsync(local, root,
                cancellationToken, contentProvider: pair.Provider, expectedPairId: pair.Id);
            var requestFingerprint = Hash("create-remote", pair.Id, pair.DropboxFolderId, path,
                sourceVersion.ObjectVersionId, sourceVersion.ContentHash!, sourceVersion.Size!.Value.ToString(CultureInfo.InvariantCulture));
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, requestFingerprint,
                "dropbox-upload-add", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-upload");
            dispatched = true;

            PilotUploadResult uploaded;
            // A small Dropbox create joins one shared add-only batch commit; it reports per batch, not per chunk.
            var batch = batchedAddOnly && uploadSession is null ? remoteStore as DropboxPilotClient : null;
            await using var uploadProgress = batch is null
                ? new SyncByteProgressStream(source, "Reading bytes for upload") : null;
            if (uploadProgress is not null) await uploadProgress.StartedAsync();
            try
            {
                uploaded = batch is not null
                    ? await batch.UploadBatchedCreateAsync(path, source, sourceVersion.ContentHash!, cancellationToken)
                    : uploadSession is null
                    ? await remoteStore.UploadAsync(path, uploadProgress!, sourceVersion.ContentHash!, null,
                        cancellationToken)
                    : await ((ISyncResumableRemoteStore)remoteStore).UploadResumableAsync(path, uploadProgress!,
                        sourceVersion.ContentHash!, null, uploadSession.Value.Checkpoint,
                        uploadSession.Value.Coordinator.PersistAsync, cancellationToken);
                if (uploadSession is not null)
                    await uploadSession.Value.Coordinator.CompleteAsync(cancellationToken);
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, SourceJson(local), "{}",
                    JsonSerializer.Serialize(new { classification = "provider_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("known-not-applied", requestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"reason\":\"The provider rejected the add-only request; re-observation is required.\"}",
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, true,
                    "Dropbox rejected the add-only request without applying it." + " " + SyncActivityText.Failure(exception));
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
                    SourceJson(local), "{}", JsonSerializer.Serialize(new { classification = "response_unknown" }),
                    Hash("effect-unknown", requestFingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true, "The upload response is unknown and must be reconciled before retry.");
            }

            await PilotLocalScanner.VerifyHeldSourceAfterUploadAsync(source, local, root, cancellationToken, pair.Provider);
            var destinationJson = JsonSerializer.Serialize(new
            {
                fileId = uploaded.FileId,
                revision = uploaded.Revision,
                uploaded.Size,
                uploaded.ContentHash,
                path
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownSuccess,
                SourceJson(local), destinationJson, destinationJson,
                Hash("known-success", requestFingerprint, destinationJson), cancellationToken);
            var remoteIdentity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot,
                uploaded.FileId, uploaded.Revision);
            var remoteVersionId = Id("version-dropbox", operation.ObjectId!, remoteIdentity,
                uploaded.ContentHash, uploaded.Size.ToString(CultureInfo.InvariantCulture));
            var remoteGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var remoteMembershipId = Id("membership-dropbox-effect", operationId, path, remoteVersionId);
            await authority.RegisterObjectVersionAsync(remoteVersionId, operation.ObjectId!, "dropbox",
                remoteIdentity, uploaded.ContentHash, uploaded.Size, cancellationToken);
            await authority.RegisterMembershipAsync(remoteMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, remoteGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operationId, sourceVersion.ObjectVersionId, remoteVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, sourceVersion.ObjectVersionId, remoteVersionId, sourceMember.MembershipId,
                remoteMembershipId, "{\"result\":\"remote create verified\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease, "{\"result\":\"remote create complete\"}",
                cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The add-only Dropbox upload and synchronization point were verified.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<(SyncAuthorityUploadSessionCoordinator Coordinator,
        SyncUploadSessionCheckpoint? Checkpoint)?> PrepareUploadSessionAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion sourceVersion,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var allocation = operation.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.UploadSession);
        var requiresSession = SyncUploadLimits.ForStore(remoteStore).RequiresSession(sourceVersion.Size,
            replacement: false);
        if (!requiresSession)
        {
            if (allocation is not null)
                throw new InvalidDataException("The retained create plan reserves a session that this destination's transfer mechanism does not use. Review a fresh plan; no content was uploaded.");
            return null;
        }
        if (allocation is null || remoteStore is not ISyncResumableRemoteStore)
            throw new InvalidOperationException(
                "The large remote-create source has no resumable provider allocation or implementation.");
        var coordinator = new SyncAuthorityUploadSessionCoordinator(authority, allocation, sourceVersion, lease);
        var checkpoint = await coordinator.PrepareAsync(cancellationToken);
        return (coordinator, checkpoint);
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion sourceVersion, string path,
        SyncAuthorityRetainedRootAuthority retainedRemoteRoot, string workerId, CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var localScan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                refreshPaths: [path], traverseDirectory: _ => false, contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!localScan.Entries.TryGetValue(path, out var local) || local.Kind != PilotEntryKind.Directory)
                throw new IOException("The exact local directory source is no longer present.");
            RequireExactLocalDirectory(local, sourceVersion);
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            await SyncByteProgressStream.ReportProcessAsync("Checking destination", path,
                "Reading the exact destination metadata for this path to verify this folder is still absent before creating it; no create request has been sent.");
            if (await remoteStore.FindEntryAtPathAsync(path, cancellationToken) is not null)
                throw new IOException("The Dropbox folder destination is no longer absent.");
            var requestFingerprint = Hash("create-remote-directory", pair.Id, pair.DropboxFolderId, path,
                sourceVersion.ObjectVersionId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                requestFingerprint, "dropbox-create-folder", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-folder-create");
            dispatched = true;
            PilotRemoteEntry created;
            try
            {
                created = await remoteStore.CreateDirectoryAsync(path, cancellationToken);
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, SourceJson(local), "{}",
                    JsonSerializer.Serialize(new { classification = "provider_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("known-not-applied", requestFingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"reason\":\"Dropbox rejected the create-folder request; re-observation is required.\"}",
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Blocked, true,
                    "Dropbox rejected the create-folder request without applying it." + " " + SyncActivityText.Failure(exception));
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, SourceJson(local), "{}",
                    "{\"classification\":\"response_unknown\"}",
                    Hash("effect-unknown", requestFingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The folder-create response is unknown and must be reconciled before retry.");
            }
            RequireCreatedDirectory(created, path);
            var destinationJson = JsonSerializer.Serialize(CurrentState(created));
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, SourceJson(local), destinationJson,
                destinationJson, Hash("known-success", requestFingerprint, destinationJson),
                cancellationToken);
            var remoteIdentity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot,
                created.Id!, null);
            var remoteVersionId = Id("version-dropbox", operation.ObjectId!, remoteIdentity, "none", "0");
            var remoteGeneration = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                path, cancellationToken);
            var remoteMembershipId = Id("membership-dropbox-effect", operation.OperationId, path,
                remoteVersionId);
            await authority.RegisterObjectVersionAsync(remoteVersionId, operation.ObjectId!, "dropbox",
                remoteIdentity, null, 0, cancellationToken);
            await authority.RegisterMembershipAsync(remoteMembershipId, operation.ObjectId!,
                destinationMember.RootBindingId, path, remoteGeneration, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-effect", operation.OperationId, sourceVersion.ObjectVersionId,
                remoteVersionId);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, sourceVersion.ObjectVersionId, remoteVersionId, sourceMember.MembershipId,
                remoteMembershipId, "{\"result\":\"remote directory create verified\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"remote directory create complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact local directory identity was created in Dropbox and verified.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private static void RequireExactLocalSource(PilotLocalEntry current, SyncAuthorityObjectVersion expected)
    {
        if (expected.Side != "local" || current.VolumeSerialNumber is null || current.FileId is null ||
            expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local source authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var root = identity.RootElement;
        var expectedVolume = uint.Parse(root.GetProperty("volumeSerial").GetString()!, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        var expectedFile = ulong.Parse(root.GetProperty("fileId").GetString()!, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        var expectedWrite = DateTime.Parse(root.GetProperty("lastWriteUtc").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (current.VolumeSerialNumber != expectedVolume || current.FileId != expectedFile)
            throw new IOException("The exact local source identity differs from the immutable operation member.");
        if (current.Size != expected.Size || current.LastWriteUtc.ToUniversalTime() != expectedWrite.ToUniversalTime() ||
            !string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthoritySourceChangedException("The exact local source content differs from the immutable operation member.");
    }

    private static void RequireExactLocalDirectory(PilotLocalEntry current,
        SyncAuthorityObjectVersion expected)
    {
        if (expected.Side != "local" || current.Kind != PilotEntryKind.Directory ||
            current.VolumeSerialNumber is null || current.FileId is null || expected.ContentHash is not null ||
            expected.Size is not 0)
            throw new InvalidDataException("The retained local directory authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var root = identity.RootElement;
        var expectedVolume = uint.Parse(root.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var expectedFile = ulong.Parse(root.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (current.VolumeSerialNumber != expectedVolume || current.FileId != expectedFile)
            throw new IOException("The exact local directory differs from the immutable operation member.");
    }

    private static void RequireCreatedDirectory(PilotRemoteEntry created, string path)
    {
        if (created.Kind != PilotEntryKind.Directory || string.IsNullOrWhiteSpace(created.Id) ||
            !string.Equals(created.RelativePath, path, StringComparison.OrdinalIgnoreCase))
            throw new DropboxEffectUncertainException(
                "Dropbox returned a different folder identity after create.");
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

    private static string SourceJson(PilotLocalEntry source) => JsonSerializer.Serialize(new
    {
        source.RelativePath,
        source.VolumeSerialNumber,
        source.FileId,
        source.LastWriteUtc,
        source.Size,
        source.ContentHash
    });

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
