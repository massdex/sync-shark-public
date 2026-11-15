using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityMoveLocalExecutor(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority)
{
    internal static Action<string>? PhaseForTest { get; set; }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.MoveLocal || operation.PairId != pair.Id ||
            operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new InvalidOperationException("The requested authority operation is not a runnable local move for this pair.");
        var sourceMember = operation.Members.Single(item => item.Role == "source");
        var destinationMember = operation.Members.Single(item => item.Role == "destination");
        var remoteVersion = await authority.LoadObjectVersionAsync(sourceMember.ObjectVersionId
            ?? throw new InvalidDataException("The local move has no exact Dropbox source version."), cancellationToken);
        var localVersion = await authority.LoadObjectVersionAsync(destinationMember.ObjectVersionId
            ?? throw new InvalidDataException("The local move has no exact local predecessor version."), cancellationToken);
        var desiredPath = PilotPath.NormalizeRelative(sourceMember.ExpectedPathKey
            ?? throw new InvalidDataException("The desired local move path is absent."));
        var priorPath = PilotPath.NormalizeRelative(destinationMember.ExpectedPathKey
            ?? throw new InvalidDataException("The prior local move path is absent."));
        if (string.Equals(desiredPath, priorPath, StringComparison.Ordinal))
            throw new InvalidDataException("The retained local move does not change membership.");
        if (localVersion.ContentHash is null)
            return await ExecuteDirectoryAsync(operation, sourceMember, destinationMember, remoteVersion,
                localVersion, priorPath, desiredPath, workerId, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var selectedRoot = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
            var priorFullPath = PilotPath.ResolveContainedPath(selectedRoot.Path, priorPath);
            var desiredFullPath = PilotPath.ResolveContainedPath(selectedRoot.Path, desiredPath);
            var sameCanonicalMembership = string.Equals(PilotPath.CanonicalComparisonKey(priorPath),
                PilotPath.CanonicalComparisonKey(desiredPath), StringComparison.Ordinal);
            if (!sameCanonicalMembership && (File.Exists(desiredFullPath) || Directory.Exists(desiredFullPath)))
                throw new IOException("The desired local move membership is not absent.");
            await using var local = SyncLocalFileAuthority.OpenExistingExclusive(priorFullPath);
            var identity = await VerifyExactLocalAsync(pair.Provider, local, priorFullPath, localVersion, selectedRoot,
                cancellationToken);
            var fingerprint = Hash("move-local", pair.Id, operation.ObjectId!, priorPath, desiredPath,
                localVersion.ObjectVersionId, remoteVersion.ObjectVersionId, Format(identity));
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
                "local-identity-move", cancellationToken);
            dispatched = true;
            local.RenameTo(selectedRoot, desiredPath);
            PhaseForTest?.Invoke("local-moved-before-receipt");
            await VerifyExactLocalAsync(pair.Provider, local, desiredFullPath, localVersion, selectedRoot, cancellationToken);
            if (!sameCanonicalMembership && (File.Exists(priorFullPath) || Directory.Exists(priorFullPath)))
                throw new IOException("The prior local move membership remains present.");
            await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
            var destinationJson = JsonSerializer.Serialize(new
            {
                priorPath,
                desiredPath,
                identity = Format(identity),
                localObjectVersionId = localVersion.ObjectVersionId,
                dropboxObjectVersionId = remoteVersion.ObjectVersionId
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(localVersion), destinationJson,
                destinationJson, Hash("move-local-success", fingerprint, destinationJson), cancellationToken);
            var generation = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                desiredPath, cancellationToken);
            var membershipId = Id("membership-local-move", operationId, desiredPath,
                localVersion.ObjectVersionId);
            await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
                destinationMember.RootBindingId, desiredPath, generation, cancellationToken: cancellationToken);
            // The moved local file still holds the baseline content: pair it with the Dropbox baseline
            // version so a Dropbox edit carried by this move is planned as the ordinary update next.
            var pointRemoteVersion = await authority.LoadMoveBaselineVersionAsync(operation, "dropbox",
                cancellationToken);
            var pointId = Id("syncpoint-move", operationId, localVersion.ObjectVersionId,
                pointRemoteVersion, desiredPath);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersion.ObjectVersionId, pointRemoteVersion, membershipId,
                sourceMember.MembershipId, "{\"result\":\"local stable identity moved to Dropbox path\"}",
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local move complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact NTFS file identity moved atomically into the Dropbox membership path.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityMember sourceMember,
        SyncAuthorityMember destinationMember, SyncAuthorityObjectVersion remoteVersion,
        SyncAuthorityObjectVersion localVersion, string priorPath, string desiredPath, string workerId,
        CancellationToken cancellationToken)
    {
        if (localVersion.Side != "local" || localVersion.ContentHash is not null || localVersion.Size is not 0)
            throw new InvalidDataException("The retained local directory move authority is incomplete.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            using var root = PilotDirectoryLease.Open(pair.LocalRoot,
                new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
            var priorFullPath = PilotPath.ResolveContainedPath(root.Path, priorPath);
            var desiredFullPath = PilotPath.ResolveContainedPath(root.Path, desiredPath);
            var sameCanonicalMembership = string.Equals(PilotPath.CanonicalComparisonKey(priorPath),
                PilotPath.CanonicalComparisonKey(desiredPath), StringComparison.Ordinal);
            if (!sameCanonicalMembership && (File.Exists(desiredFullPath) || Directory.Exists(desiredFullPath)))
                throw new IOException("The desired local directory membership is not absent.");
            var identity = LocalDirectoryIdentity(localVersion);
            using var local = PilotDirectoryLease.OpenOwnedDirectory(priorFullPath, identity);
            var fingerprint = Hash("move-local-directory", pair.Id, operation.ObjectId!, priorPath,
                desiredPath, localVersion.ObjectVersionId, remoteVersion.ObjectVersionId, Format(identity));
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                "local-directory-identity-move", cancellationToken);
            dispatched = true;
            local.RenameTo(root, desiredPath);
            PhaseForTest?.Invoke("local-directory-moved-before-receipt");
            if (!sameCanonicalMembership && (File.Exists(priorFullPath) || Directory.Exists(priorFullPath)) ||
                !Directory.Exists(desiredFullPath) ||
                !PilotPath.HasExactRelativePath(root.Path, PilotDirectoryLease.FinalPath(local.Handle), desiredPath))
                throw new IOException("The exact local directory move membership was not established.");
            local.VerifyBinding();
            await RequireExactRemoteAsync(desiredPath, remoteVersion, cancellationToken);
            var destinationJson = JsonSerializer.Serialize(new
            {
                priorPath,
                desiredPath,
                kind = "directory",
                identity = Format(identity),
                localObjectVersionId = localVersion.ObjectVersionId,
                dropboxObjectVersionId = remoteVersion.ObjectVersionId
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, LocalVersionJson(localVersion), destinationJson,
                destinationJson, Hash("move-local-directory-success", fingerprint, destinationJson),
                cancellationToken);
            var generation = await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                desiredPath, cancellationToken);
            var membershipId = Id("membership-local-move", operation.OperationId, desiredPath,
                localVersion.ObjectVersionId);
            await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
                destinationMember.RootBindingId, desiredPath, generation, cancellationToken: cancellationToken);
            var pointId = Id("syncpoint-move", operation.OperationId, localVersion.ObjectVersionId,
                remoteVersion.ObjectVersionId, desiredPath);
            await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, pair.Id,
                operation.ObjectId!, localVersion.ObjectVersionId, remoteVersion.ObjectVersionId, membershipId,
                sourceMember.MembershipId, "{\"result\":\"local directory identity moved to Dropbox path\"}",
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"local directory move complete\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Completed, true,
                "The exact NTFS directory identity moved atomically into the Dropbox membership path.");
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task RequireExactRemoteAsync(string path, SyncAuthorityObjectVersion expected,
        CancellationToken cancellationToken)
    {
        if (expected.Side != "dropbox")
            throw new InvalidDataException("The retained Dropbox move authority is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var fileId = identity.RootElement.GetProperty("fileId").GetString();
        var revision = identity.RootElement.GetProperty("revision").GetString();
        await remoteStore.VerifyRootIdentityAsync(cancellationToken);
        var matches = (await remoteStore.ListEntriesAsync(cancellationToken)).Where(item =>
            string.Equals(item.RelativePath, path, StringComparison.Ordinal)).ToArray();
        var exact = matches.Length == 1 && string.Equals(matches[0].Id, fileId, StringComparison.Ordinal) &&
                    (expected.ContentHash is null
                        ? matches[0].Kind == PilotEntryKind.Directory && expected.Size is 0
                        : matches[0].Kind == PilotEntryKind.File && expected.Size is not null &&
                          string.Equals(matches[0].Revision, revision, StringComparison.Ordinal) &&
                          matches[0].Size == expected.Size && string.Equals(matches[0].ContentHash,
                              expected.ContentHash, StringComparison.OrdinalIgnoreCase));
        if (!exact)
            throw new IOException("The Dropbox move source differs from the immutable operation member.");
    }

    internal static PilotDirectoryIdentity LocalDirectoryIdentity(SyncAuthorityObjectVersion expected)
    {
        if (expected.Side != "local" || expected.ContentHash is not null || expected.Size is not 0)
            throw new InvalidDataException("The retained local directory move authority is incomplete.");
        using var document = JsonDocument.Parse(expected.IdentityJson);
        return new PilotDirectoryIdentity(
            uint.Parse(document.RootElement.GetProperty("volumeSerial").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(document.RootElement.GetProperty("fileId").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    internal static async Task<PilotDirectoryIdentity> VerifyExactLocalAsync(string? contentProvider, SyncLocalFileAuthority local,
        string path, SyncAuthorityObjectVersion expected, PilotDirectoryLease root,
        CancellationToken cancellationToken)
    {
        if (expected.Side != "local" || expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained local move authority is incomplete.");
        using var document = JsonDocument.Parse(expected.IdentityJson);
        var expectedIdentity = new PilotDirectoryIdentity(
            uint.Parse(document.RootElement.GetProperty("volumeSerial").GetString()!, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture),
            ulong.Parse(document.RootElement.GetProperty("fileId").GetString()!, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture));
        if (local.VerifySingleLink() != expectedIdentity || local.Stream.Length != expected.Size ||
            !PilotPath.HasExactRelativePath(root.Path, local.FinalPath,
                PilotPath.NormalizeRelative(Path.GetRelativePath(root.Path, path))))
            throw new IOException("The local move object differs from the immutable member identity.");
        local.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  local.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local move bytes differ from the immutable member version.");
        root.VerifyBinding();
        return expectedIdentity;
    }

    internal static string LocalVersionJson(SyncAuthorityObjectVersion version) => JsonSerializer.Serialize(new
    {
        version.ObjectVersionId, version.IdentityJson, version.ContentHash, version.Size
    });
    internal static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
    internal static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];
    internal static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
