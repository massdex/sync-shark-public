using System.Text.Json;

namespace Massdex.SyncShark;

// Completes an already-applied cloud move whose earlier first-sync plan copied
// B but left A behind. This retires only A's obsolete membership. It does not
// delete the logical object, replay the copy, synthesize absence from a partial
// inventory, or clear immutable history. Ordinary deletion/recovery owns the
// actual same-volume retention effect and restart reconciliation.
internal static class SyncAuthorityCompletedCopyMove
{
    internal static string OperationId(string pairId, SyncCompletedCopyMove proof) =>
        SyncAuthorityMoveLocalExecutor.Id("operation", "completed-copy-move", pairId,
            proof.PriorPointId, proof.CurrentPointId, proof.CopyOperationId);

    internal static string GroupId(string pairId, SyncCompletedCopyMove proof) =>
        SyncAuthorityMoveLocalExecutor.Id("group", OperationId(pairId, proof));

    internal static SyncCompletedCopyMove? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("completedCopyMove", out var value)
            ? value.Deserialize<SyncCompletedCopyMove>() ?? throw new InvalidDataException("Incomplete completed-copy move authority.")
            : null;
    }

    internal static async Task<SyncLocalFileAuthority> HoldVerifiedCopyAsync(SyncPair pair,
        ISyncRemoteStore? remote, SyncAuthorityDatabase authority, PilotDirectoryLease root,
        SyncCompletedCopyMove proof, CancellationToken token)
    {
        if (remote is null || pair.Provider != SyncProviders.Dropbox ||
            PilotPath.NormalizeRelative(proof.OldPath) == PilotPath.NormalizeRelative(proof.NewPath))
            throw new InvalidDataException("Completed-copy retirement requires one bound Dropbox move and its distinct local paths.");
        if (!await authority.CompletedCopyMoveMatchesAsync(pair.Id, proof, token))
            throw new InvalidDataException("The completed-copy move lineage changed; the obsolete file was preserved.");
        var oldLocal = await authority.LoadObjectVersionAsync(proof.OldVersionId, token);
        var newLocal = await authority.LoadObjectVersionAsync(proof.NewVersionId, token);
        var oldCloud = await authority.LoadObjectVersionAsync(proof.OldRemoteVersionId, token);
        var newCloud = await authority.LoadObjectVersionAsync(proof.NewRemoteVersionId, token);
        if (new[] { oldLocal, newLocal, oldCloud, newCloud }.Any(v => v.ObjectId != proof.ObjectId ||
                v.ContentHash is null || v.Size is null) || oldLocal.Side != "local" || newLocal.Side != "local" ||
            oldCloud.Side != "dropbox" || newCloud.Side != "dropbox" ||
            oldLocal.IdentityJson == newLocal.IdentityJson || !SameBytes(oldLocal, oldCloud) || !SameBytes(newLocal, newCloud))
            throw new InvalidDataException("The retained predecessor and completed copy are not two exact accepted file versions.");
        using var priorIdentity = JsonDocument.Parse(oldCloud.IdentityJson);
        using var currentIdentity = JsonDocument.Parse(newCloud.IdentityJson);
        var providerId = currentIdentity.RootElement.GetProperty("fileId").GetString()!;
        if (providerId != priorIdentity.RootElement.GetProperty("fileId").GetString() ||
            !await authority.CompletedCopyReceiptMatchesAsync(proof.CopyOperationId,
                newLocal.IdentityJson, proof.NewPath, token))
            throw new InvalidDataException("The moved cloud object or completed physical copy is not backed by the retained success receipt.");
        await remote.VerifyRootIdentityAsync(token);
        var current = await remote.ReadEntryAsync(proof.NewPath, providerId, token);
        if (current.Kind != PilotEntryKind.File || current.Id != providerId || current.RelativePath != proof.NewPath ||
            current.Revision != currentIdentity.RootElement.GetProperty("revision").GetString() ||
            current.Size != newCloud.Size || !string.Equals(current.ContentHash, newCloud.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The moved cloud file changed after its copy completed; both local versions were preserved.");
        var oldPathOccupant = await remote.FindEntryAtPathAsync(proof.OldPath, token);
        if (oldPathOccupant?.Id == providerId)
            throw new IOException("The cloud object returned to its old path; the local predecessor was preserved.");
        var copyPath = PilotPath.ResolveContainedPath(root.Path, proof.NewPath);
        var copy = SyncLocalFileAuthority.OpenExistingExclusive(copyPath);
        try
        {
            var copyIdentity = await SyncAuthorityMoveLocalExecutor.VerifyExactLocalAsync(pair.Provider,
                copy, copyPath, newLocal, root, token);
            using var oldIdentity = JsonDocument.Parse(oldLocal.IdentityJson);
            if (SyncAuthorityMoveLocalExecutor.Format(copyIdentity) ==
                oldIdentity.RootElement.GetProperty("volumeSerial").GetString() + ":" + oldIdentity.RootElement.GetProperty("fileId").GetString())
                throw new InvalidDataException("The predecessor and replacement share a physical identity.");
            await remote.VerifyRootIdentityAsync(token);
            root.VerifyBinding();
            return copy;
        }
        catch { await copy.DisposeAsync(); throw; }
    }

    internal static async Task<string> PlanAsync(SyncPair pair, ISyncRemoteStore remote,
        SyncAuthorityDatabase authority, SyncAuthorityPlannerOptions options,
        SyncCompletedCopyMove proof, SyncAuthorityCommandAdmission admission, CancellationToken token)
    {
        if (options.CloudAllocations is not null || !SyncModePolicy.Allows(options.Direction, SyncAuthorityOperationKind.DeleteLocal))
            throw new InvalidDataException("The current pair direction does not allow mirroring this cloud move locally.");
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        await using var copy = await HoldVerifiedCopyAsync(pair, remote, authority, root, proof, token);
        var old = await authority.LoadObjectVersionAsync(proof.OldVersionId, token);
        var oldPath = PilotPath.ResolveContainedPath(root.Path, proof.OldPath);
        await using var predecessor = SyncLocalFileAuthority.OpenExistingExclusive(oldPath);
        await SyncAuthorityMoveLocalExecutor.VerifyExactLocalAsync(pair.Provider, predecessor, oldPath, old, root, token);
        var localRoot = await authority.LoadActiveRootBindingIdAsync(pair.Id, "local", token);
        var remoteRoot = await authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox", token);
        var set = await authority.LoadPlanningObservationSetAsync(pair.Id, options.ConfigurationRevision, localRoot, remoteRoot,
            await authority.LoadLatestCompleteScanIdAsync(pair.Id, "local", token),
            await authority.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox", token), token);
        var idempotencyKey = SyncAuthorityMoveLocalExecutor.Id("idempotency", OperationId(pair.Id, proof));
        var registration = await authority.ReserveActionAsync(idempotencyKey, admission, token);
        var operationId = registration.OperationId;
        var groupId = GroupId(pair.Id, proof);
        await authority.RegisterOperationGroupAsync(groupId, "recovery",
            JsonSerializer.Serialize(new { completedCopyMove = proof }), token);
        var members = new SyncAuthorityMember[]
        {
            new(SyncAuthorityMoveLocalExecutor.Id("member", operationId, "source"), pair.Id, proof.ObjectId,
                "source", localRoot, proof.OldMembershipId, proof.OldVersionId, proof.OldMembershipGeneration,
                proof.OldPath, JsonSerializer.Serialize(new { present = true, old.ObjectVersionId, old.ContentHash, old.Size })),
            new(SyncAuthorityMoveLocalExecutor.Id("member", operationId, "lock"), pair.Id, proof.ObjectId,
                "lock", localRoot, proof.NewMembershipId, proof.NewVersionId, proof.NewMembershipGeneration,
                proof.NewPath, "{\"present\":true}")
        };
        // Only this exact verified compact proof replaces the historical FK.
        // Registration rechecks it in the same transaction as operation admission.
        SyncAuthorityDependency[] dependencies =
            await authority.IsNormalizedCompletedCopyMoveAsync(pair.Id, proof, token)
                ? [] : [new(proof.CopyOperationId, "must_complete")];
        await authority.CreateOperationAsync(new(operationId, idempotencyKey,
            SyncAuthorityOperationKind.DeleteLocal, pair.Id, proof.ObjectId, options.ConfigurationRevision,
            options.CapabilityProfileId, JsonSerializer.Serialize(new { completedCopyMove = proof }), 90,
            members, dependencies,
            SyncAuthorityOperationAllocations.Build(operationId, SyncAuthorityOperationKind.DeleteLocal, proof.OldPath,
                localRoot, remoteRoot, "file", old.Size, options),
            groupId,
            [set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set), proof.ObjectId)]), admission, token, reservation: registration.Reservation);
        // This bounded recovery group has exactly this one fully retained
        // operation. An interruption before sealing admits no effect.
        await authority.MarkOperationGroupActiveAsync(groupId, token);
        return operationId;
    }

    private static bool SameBytes(SyncAuthorityObjectVersion left, SyncAuthorityObjectVersion right) =>
        left.Size == right.Size && string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase);
}
