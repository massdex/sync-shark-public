using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityMoveRemoteExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    internal static Action<string>? PhaseForTest { get; set; }
    internal const string SuccessorIdentityConflict = "native_move_applied_successor_identity_conflict";

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.MoveRemote || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
                SyncAuthorityOperationStatus.Validating))
            throw new InvalidOperationException("The requested authority operation is not a runnable remote move for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        _ = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            destinationMember.RootBindingId, cancellationToken);
        var localVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote-move source version is absent."), cancellationToken);
        var remoteVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The remote-move predecessor version is absent."), cancellationToken);
        var desiredPath = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The desired remote path is absent."));
        var priorPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey
            ?? throw new InvalidDataException("The prior remote path is absent."));
        if (string.Equals(desiredPath, priorPath, StringComparison.Ordinal))
            throw new InvalidDataException("The retained remote move does not change membership.");

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            // One exact scoped root proof for the single local source: the exact desired path only, with no
            // directory traversal and no sibling or descendant enumeration. A directory source therefore
            // proves its verified root/ancestor/reparse/cloud/NTFS identity and metadata without reading or
            // hashing anything else, while a file source keeps its exact held-bytes content proof.
            var scan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                includePath: item => string.Equals(PilotPath.CanonicalComparisonKey(item),
                    PilotPath.CanonicalComparisonKey(desiredPath), StringComparison.Ordinal),
                traverseDirectory: _ => false, refreshPaths: [desiredPath],
                contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!scan.Entries.TryGetValue(desiredPath, out var local) ||
                !string.Equals(local.RelativePath, desiredPath, StringComparison.Ordinal))
                throw new IOException("The exact local move source is absent.");
            RequireExactLocal(local, localVersion);
            var predecessor = await LoadExactRemoteAsync(priorPath, remoteVersion, cancellationToken);
            await RequireAbsentRemotePathAsync(desiredPath, predecessor.Id, cancellationToken);
            var fingerprint = Hash("move-remote-native", pair.Id, operation.ObjectId!, predecessor.Id!,
                predecessor.Revision ?? "directory", priorPath, desiredPath, localVersion.ObjectVersionId);
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
                "dropbox-native-move", cancellationToken);
            dispatched = true;
            PilotRemoteEntry moved;
            try
            {
                moved = await remoteStore.MoveAsync(predecessor, desiredPath, cancellationToken);
            }
            catch (DropboxApiException exception)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(predecessor), "{}",
                    JsonSerializer.Serialize(new { classification = "provider_rejected", exception.StatusCode, reason = SyncActivityText.Failure(exception) }),
                    Hash("native-move-not-applied", fingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"reason\":\"Dropbox rejected the identity-preserving move.\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked,
                    true, "Dropbox rejected the identity-preserving move without applying it." + " " + SyncActivityText.Failure(exception));
            }
            catch (DropboxEffectUncertainException)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, RemoteJson(predecessor), "{}",
                    "{\"classification\":\"native move response unknown\"}",
                    Hash("native-move-unknown", fingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true, "The Dropbox move response is unknown and will be reconciled by stable identity.");
            }
            RequireMovedIdentity(predecessor, moved, desiredPath);
            PhaseForTest?.Invoke("remote-native-move-before-receipt");
            RemoteSuccessor successor;
            try
            {
                successor = await RegisterSuccessorAsync(operation, destinationMember.RootBindingId,
                    desiredPath, moved, cancellationToken);
            }
            catch (SyncObjectVersionIdentityConflictException)
            {
                // The provider effect is known even when its returned immutable version conflicts
                // with retained state. Keep raw evidence, never a nonexistent successor reference.
                var conflictJson = JsonSerializer.Serialize(new
                {
                    classification = SuccessorIdentityConflict,
                    moved.Id, moved.RelativePath, moved.Kind, moved.Revision, moved.Size, moved.ContentHash
                });
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(predecessor), conflictJson,
                    conflictJson, Hash("native-move-success-identity-conflict", fingerprint, conflictJson),
                    cancellationToken);
                PhaseForTest?.Invoke("remote-native-move-identity-conflict-receipt");
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked, conflictJson, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked,
                    true, "Dropbox moved the file and its latest contents; retained version identity conflicts, " +
                    "so synchronization needs a fresh comparison.");
            }
            var resultJson = SuccessorJson(successor);
            // SS-D17 completion: the returned contents are compared with the ORIGINAL retained remote
            // member, not merely with the freshly read predecessor. An unchanged object keeps the existing
            // known-success receipt, successor membership/version, synchronization point and completion. An
            // object whose latest contents differ is still a known successful membership effect, so it retains
            // its actual successor and a known-success receipt and then uses the existing fenced blocked
            // reconciliation with an explicit classification. It never inserts a synchronization point
            // claiming the original local bytes equal the changed remote bytes, never synthesizes a no-effect
            // receipt and never permits the move to be issued again.
            if (RetainedRemoteContentDiffers(moved, remoteVersion))
            {
                var pendingJson = JsonSerializer.Serialize(new
                {
                    classification = "native_move_applied_content_comparison_pending",
                    fileId = moved.Id,
                    path = moved.RelativePath,
                    kind = moved.Kind.ToString(),
                    revision = moved.Revision,
                    size = moved.Size,
                    contentHash = moved.ContentHash
                });
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(predecessor), resultJson, pendingJson,
                    Hash("native-move-success-content-pending", fingerprint, pendingJson), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked, pendingJson, cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, true,
                    "Dropbox moved the file and its latest contents; synchronization needs a fresh comparison.");
            }
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(predecessor), resultJson, resultJson,
                Hash("native-move-success", fingerprint, resultJson), cancellationToken);
            // The moved Dropbox object still holds the baseline content: pair it with the local baseline
            // version so a local edit carried by this move is planned as the ordinary update next.
            var pointLocalVersion = await authority.LoadMoveBaselineVersionAsync(operation, "local",
                cancellationToken);
            var pointId = Id("syncpoint-move", operationId, pointLocalVersion,
                successor.ObjectVersionId, desiredPath);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, pointLocalVersion, successor.ObjectVersionId,
                sourceMember.MembershipId, successor.MembershipId,
                "{\"result\":\"identity-preserving remote move committed\"}", cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"identity-preserving remote move complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "Dropbox moved the existing object without re-uploading or deleting its content.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    internal async Task<RemoteSuccessor> RegisterSuccessorAsync(SyncAuthorityOperationSnapshot operation,
        string rootBindingId, string desiredPath, PilotRemoteEntry moved,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moved.Id))
            throw new InvalidDataException("The moved Dropbox object has no stable identity.");
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair,
            rootBindingId, cancellationToken);
        var identity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot, moved.Id, moved.Revision);
        var versionId = Id("version-dropbox", operation.ObjectId!, identity, moved.ContentHash ?? "none",
            moved.Size.ToString(CultureInfo.InvariantCulture));
        versionId = await authority.RegisterObjectVersionExactAsync(versionId, operation.ObjectId!,
            "dropbox", identity, moved.ContentHash, moved.Size, cancellationToken);
        var membershipId = Id("membership-dropbox-move", operation.OperationId, desiredPath, versionId);
        var generation = await authority.LoadMembershipGenerationAsync(membershipId, cancellationToken) ??
                         await authority.NextMembershipGenerationAsync(rootBindingId, desiredPath,
                             cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!, rootBindingId, desiredPath,
            generation, cancellationToken: cancellationToken);
        return new RemoteSuccessor(moved.Id, moved.Revision, moved.Size, moved.ContentHash, moved.Kind,
            versionId, membershipId);
    }

    internal static void RequireExactLocal(PilotLocalEntry current, SyncAuthorityObjectVersion expected)
    {
        if (expected.Side != "local" || current.VolumeSerialNumber is null || current.FileId is null)
            throw new InvalidDataException("The retained local remote-move source authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var volume = uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var file = ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var contentMatches = current.Kind == PilotEntryKind.Directory
            ? expected.ContentHash is null && expected.Size is 0
            : expected.ContentHash is not null && expected.Size is not null && current.Size == expected.Size &&
              string.Equals(current.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase);
        if (current.VolumeSerialNumber != volume || current.FileId != file || !contentMatches)
            throw new IOException("The local remote-move source differs from its immutable member.");
    }

    internal async Task<PilotRemoteEntry> LoadExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var id = identity.RootElement.GetProperty("fileId").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        // One fresh identity-conditioned read at this exact path; the provider is never
        // asked to enumerate the whole destination tree to find the move predecessor.
        var current = await remoteStore.ReadEntryAsync(path, id
                ?? throw new InvalidDataException("The prior Dropbox move membership has no exact object identity."),
            cancellationToken);
        // The scoped stable identity, the exact path and the kind are the immutable part of the membership.
        // The provider's current revision, size and content hash are accepted as the object's latest
        // contents, because the accepted native-move policy carries whatever the stable object holds now.
        var expectedKind = expected.ContentHash is null ? PilotEntryKind.Directory : PilotEntryKind.File;
        var exact = string.Equals(current.Id, id, StringComparison.Ordinal) &&
                    string.Equals(current.RelativePath, path, StringComparison.Ordinal) &&
                    current.Kind == expectedKind;
        if (!exact)
            throw new IOException("The prior Dropbox move membership differs from its immutable member.");
        return current;
    }

    // The completion comparison is against the retained remote member's original contents, never against
    // the just-read predecessor. A directory member has no content to compare.
    internal static bool RetainedRemoteContentDiffers(PilotRemoteEntry moved,
        SyncAuthorityObjectVersion retained)
    {
        if (retained.ContentHash is null) return false;
        using var identity = JsonDocument.Parse(retained.IdentityJson);
        var revision = identity.RootElement.TryGetProperty("revision", out var revisionElement)
            ? revisionElement.GetString()
            : null;
        return !string.Equals(moved.Revision, revision, StringComparison.Ordinal) ||
               moved.Size != retained.Size ||
               !string.Equals(moved.ContentHash, retained.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RequireMovedIdentity(PilotRemoteEntry predecessor, PilotRemoteEntry moved,
        string desiredPath)
    {
        var exact = string.Equals(moved.Id, predecessor.Id, StringComparison.Ordinal) &&
                    string.Equals(moved.RelativePath, desiredPath, StringComparison.Ordinal) &&
                    moved.Kind == predecessor.Kind;
        if (!exact)
            throw new DropboxEffectUncertainException(
                "Dropbox returned a different object identity or version after move.");
    }

    private async Task RequireAbsentRemotePathAsync(string path, string? movingObjectId,
        CancellationToken cancellationToken)
    {
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var occupant = await remoteStore.FindEntryAtPathAsync(path, cancellationToken);
        if (occupant is not null && !string.Equals(occupant.Id, movingObjectId, StringComparison.Ordinal))
            throw new IOException("The remote move destination is occupied.");
    }

    internal static string SuccessorJson(RemoteSuccessor successor) => JsonSerializer.Serialize(successor);
    internal static string RemoteJson(PilotRemoteEntry remote) => JsonSerializer.Serialize(new
    {
        remote.RelativePath,
        remote.Kind,
        remote.Id,
        remote.Revision,
        remote.Size,
        remote.ContentHash
    });
    internal static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];
    internal static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();

    public sealed record RemoteSuccessor(string FileId, string? Revision, long Size, string? ContentHash,
        PilotEntryKind Kind, string ObjectVersionId, string MembershipId);
}
