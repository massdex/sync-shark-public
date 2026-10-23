using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

// A new immutable operation version under the original reviewed authority, not
// a partial scan masquerading as a fresh complete inventory. The complete saved
// pair still supplies collision/move/baseline authority; fresh per-file evidence
// supplies only the same physical source's changed byte version. Ordinary
// dispatch independently checks that new version and the destination again.
internal static class SyncAuthorityReviewedSourceRefresh
{
    internal static async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> PlanAsync(
        SyncPair pair, ISyncRemoteStore remote, SyncAuthorityDatabase authority,
        SyncAuthorityPlannerOptions options, SyncAuthorityCommandAdmission admission, string localRoot, string remoteRoot, string group,
        IReadOnlyList<SyncAuthorityOperationSnapshot> originals,
        IReadOnlyDictionary<string, SyncAuthorityOperationKind> expectedKinds,
        IReadOnlyDictionary<string, string>? objectFences,
        IReadOnlyDictionary<string, string>? pathFences,
        Func<SyncComparisonProgress, Task>? progress, CancellationToken token)
    {
        var set = await authority.LoadPlanningObservationSetAsync(pair.Id, options.ConfigurationRevision,
            localRoot, remoteRoot, await authority.LoadLatestCompleteScanIdAsync(pair.Id, "local", token),
            await authority.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox", token), token);
        var setId = SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set);
        var retainedRemoteRoot = await SyncAuthorityRemoteIdentity.LoadAsync(authority, pair, remoteRoot, token);
        var results = new List<SyncAuthorityPlannerOutcome>();
        using var root = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        await remote.VerifyRootIdentityAsync(token);
        foreach (var old in originals)
        {
            token.ThrowIfCancellationRequested();
            var source = old.Members.Single(item => item.Role == "source");
            var destination = old.Members.Single(item => item.Role == "destination");
            var path = PilotPath.NormalizeRelative(source.ExpectedPathKey!);
            try
            {
                if (old.PairId != pair.Id || old.ConfigurationRevision != options.ConfigurationRevision ||
                    old.CapabilityProfileId != options.CapabilityProfileId || old.ObjectId is null ||
                    !expectedKinds.TryGetValue(path, out var expected) || expected != old.Kind ||
                    old.Kind is not (SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote
                        or SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote) ||
                    !SyncModePolicy.Allows(options.Direction, old.Kind) ||
                    (old.Kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote &&
                        !options.EnableCreateEffects) ||
                    (old.Kind is SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote &&
                        !options.EnableReplacementEffects) ||
                    old.SubtreeReservations.Count != 0 || old.Dependencies.Count != 0 ||
                    objectFences?.ContainsKey(old.ObjectId) == true ||
                    pathFences?.Keys.Any(fence => path.Equals(fence, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(fence.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) == true ||
                    !await authority.IsKnownNoEffectRefusalAsync(old.OperationId, token))
                    throw new InvalidDataException("The original reviewed file no longer has an exact same-direction, known-no-effect refresh authority.");
                if (!await authority.IsPlanningAuthorityCurrentAsync(set.Bind(setId, old.ObjectId), token))
                    throw new InvalidDataException("The retained root, baseline or collision authority changed before the source refresh.");
                var prior = await authority.LoadObjectVersionAsync(source.ObjectVersionId!, token);
                if (prior.ContentHash is null || prior.Size is null ||
                    path != PilotPath.NormalizeRelative(destination.ExpectedPathKey!) ||
                    prior.ObjectId != old.ObjectId || source.ObjectId != old.ObjectId || destination.ObjectId != old.ObjectId ||
                    source.RootBindingId != (prior.Side == "local" ? localRoot : remoteRoot) ||
                    destination.RootBindingId != (prior.Side == "local" ? remoteRoot : localRoot) ||
                    (old.Kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal) != (prior.Side == "dropbox"))
                    throw new InvalidDataException("Only one exact file at its original reviewed path may be refreshed.");
                await ParentsAsync(path, pair, remote, root, set, authority, token);
                var fresh = await ReadVersionAsync(path, prior.Side, prior.ObjectId, pair, remote, root,
                    prior.IdentityJson, retainedRemoteRoot, token);
                if (!SameIdentity(prior.Side, prior.IdentityJson, fresh.IdentityJson))
                    throw new InvalidDataException("The source physical identity changed; its replacement was never approved.");
                var destinationSide = prior.Side == "local" ? "dropbox" : "local";
                if (destination.ObjectVersionId is { } predecessorId)
                {
                    var predecessor = await authority.LoadObjectVersionAsync(predecessorId, token);
                    var actual = await ReadVersionAsync(path, destinationSide, predecessor.ObjectId,
                        pair, remote, root, predecessor.IdentityJson, retainedRemoteRoot, token);
                    if (!SameIdentity(destinationSide, predecessor.IdentityJson, actual.IdentityJson) ||
                        !string.Equals(predecessor.ContentHash, actual.ContentHash, StringComparison.OrdinalIgnoreCase) || predecessor.Size != actual.Size ||
                        (destinationSide == "dropbox" && Revision(predecessor.IdentityJson) != Revision(actual.IdentityJson)))
                        throw new InvalidDataException("The destination changed after approval; the losing version must not be overwritten.");
                }
                else if (destinationSide == "local"
                    ? File.Exists(PilotPath.ResolveContainedPath(root.Path, path)) || Directory.Exists(PilotPath.ResolveContainedPath(root.Path, path))
                    : await remote.FindEntryAtPathAsync(path, token) is not null)
                    throw new InvalidDataException("The reviewed absent destination is now occupied.");
                await remote.VerifyRootIdentityAsync(token);
                root.VerifyBinding();
                await authority.RegisterObjectVersionAsync(fresh.ObjectVersionId, fresh.ObjectId, fresh.Side,
                    fresh.IdentityJson, fresh.ContentHash, fresh.Size, token);
                var retained = await authority.LoadObjectVersionAsync(fresh.ObjectVersionId, token);
                if (retained.ObjectId != fresh.ObjectId || retained.Side != fresh.Side ||
                    retained.IdentityJson != fresh.IdentityJson || retained.ContentHash != fresh.ContentHash || retained.Size != fresh.Size)
                    throw new InvalidDataException("The refreshed source version collides with retained evidence.");
                var idempotencyKey = Id("idempotency", Id("operation", old.OperationId, fresh.ObjectVersionId, group));
                var registration = await authority.ReserveActionAsync(idempotencyKey, admission, token);
                var id = registration.OperationId;
                var members = old.Members.Select(member => member with
                {
                    MemberId = Id("member", id, member.Role),
                    ObjectVersionId = member.Role == "source" ? fresh.ObjectVersionId : member.ObjectVersionId,
                    ExpectedStateJson = member.Role == "source" ? JsonSerializer.Serialize(new
                    {
                        present = true, RelativePath = path, ObjectVersionId = fresh.ObjectVersionId,
                        source.MembershipId, source.ExpectedMembershipGeneration,
                        ContentHash = fresh.ContentHash, Size = fresh.Size, ObjectKind = "file"
                    }) : member.ExpectedStateJson
                }).ToArray();
                var preconditions = JsonNode.Parse(old.PreconditionsJson)!.AsObject();
                preconditions.Remove("planningAuthorities");
                preconditions["refreshedFromOperationId"] = old.OperationId;
                preconditions["source"] = JsonNode.Parse(members.Single(item => item.Role == "source").ExpectedStateJson);
                preconditions["observationSetId"] = setId;
                preconditions["localScanGenerationId"] = set.LocalScanGenerationId;
                preconditions["dropboxScanGenerationId"] = set.DropboxScanGenerationId;
                await authority.CreateOperationAsync(new(id, idempotencyKey, old.Kind, pair.Id,
                    old.ObjectId, options.ConfigurationRevision, options.CapabilityProfileId,
                    preconditions.ToJsonString(), 50, members, [],
                    SyncAuthorityOperationAllocations.Build(id, old.Kind, path, localRoot, remoteRoot,
                        "file", fresh.Size, options), group, [set.Bind(setId, old.ObjectId)]), admission, token, reservation: registration.Reservation);
                if (progress is not null)
                    await progress(new("Refreshed Approved Source", path, results.Count + 1, originals.Count,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                        $"Same-identity source version {fresh.ObjectVersionId} replaces refused operation {old.OperationId} in operation {id}. Root, parent, destination and collision authority checked; no whole-pair scan or file effect was performed.", "approved files"));
                results.Add(new(path, old.ObjectId, SyncAuthorityPlannerOutcomeKind.Operation, id, null, null,
                    "The same approved source was freshly versioned; the original direction and destination remain fenced."));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
            {
                results.Add(new(path, source.ObjectId, SyncAuthorityPlannerOutcomeKind.Blocked, null, null, null,
                    "Source refresh refused without effects: " + error.Message));
            }
        }
        return results;
    }

    private static async Task ParentsAsync(string path, SyncPair pair, ISyncRemoteStore remote,
        PilotDirectoryLease root, SyncAuthorityPairObservationSet set, SyncAuthorityDatabase authority,
        CancellationToken token)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            var parent = path[..slash];
            var left = set.Local.SingleOrDefault(item => item.RelativePath.Equals(parent, StringComparison.OrdinalIgnoreCase));
            var right = set.Dropbox.SingleOrDefault(item => item.RelativePath.Equals(parent, StringComparison.OrdinalIgnoreCase));
            if (left is null || right is null || left.ObjectKind != "directory" || right.ObjectKind != "directory")
                throw new InvalidDataException("A parent lacks retained two-sided directory authority; a wider inventory is required.");
            var localVersion = await authority.LoadObjectVersionAsync(left.ObjectVersionId, token);
            var remoteVersion = await authority.LoadObjectVersionAsync(right.ObjectVersionId, token);
            using var localIdentity = JsonDocument.Parse(localVersion.IdentityJson);
            using var parentLease = PilotDirectoryLease.Open(PilotPath.ResolveContainedPath(root.Path, parent),
                new PilotDirectoryIdentity(uint.Parse(localIdentity.RootElement.GetProperty("volumeSerial").GetString()!, NumberStyles.HexNumber),
                    ulong.Parse(localIdentity.RootElement.GetProperty("fileId").GetString()!, NumberStyles.HexNumber)));
            using var remoteIdentity = JsonDocument.Parse(remoteVersion.IdentityJson);
            var current = await remote.ReadEntryAsync(parent, remoteIdentity.RootElement.GetProperty("fileId").GetString()!, token);
            if (current.Kind != PilotEntryKind.Directory || current.Id != remoteIdentity.RootElement.GetProperty("fileId").GetString())
                throw new InvalidDataException("A parent identity changed; a wider inventory is required.");
            root.VerifyBinding(); parentLease.VerifyBinding();
        }
    }

    private static async Task<SyncAuthorityObjectVersion> ReadVersionAsync(string path, string side,
        string objectId, SyncPair pair, ISyncRemoteStore remote, PilotDirectoryLease root,
        string expectedIdentity, SyncAuthorityRetainedRootAuthority retainedRemoteRoot, CancellationToken token)
    {
        string identity; string? hash; long size;
        if (side == "local")
        {
            var scan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: token,
                refreshPaths: [path], traverseDirectory: _ => false, contentProvider: pair.Provider, expectedPairId: pair.Id);
            if (!scan.Entries.TryGetValue(path, out var entry) || entry.Kind != PilotEntryKind.File || entry.ContentHash is null)
                throw new InvalidDataException("The exact local file could not be observed safely.");
            identity = SyncAuthorityLocalVersionIdentity.SerializeFor(pair, entry.Kind,
                entry.VolumeSerialNumber!.Value, entry.FileId!.Value, entry.LastWriteUtc,
                entry.CloudFileId, entry.CloudRevision, entry.ContentHash, entry.Size);
            hash = entry.ContentHash; size = entry.Size;
        }
        else
        {
            using var expected = JsonDocument.Parse(expectedIdentity);
            var entry = await remote.ReadEntryAsync(path, expected.RootElement.GetProperty("fileId").GetString()!, token);
            if (entry.Kind != PilotEntryKind.File || entry.ContentHash is null || entry.Revision is null)
                throw new InvalidDataException("The exact remote file lacks verifiable byte-version evidence.");
            identity = SyncAuthorityRemoteIdentity.Serialize(retainedRemoteRoot, entry.Id!, entry.Revision);
            hash = entry.ContentHash; size = entry.Size;
        }
        return new(Id("version-" + side, objectId, identity, hash, size.ToString(CultureInfo.InvariantCulture)),
            objectId, side, identity, hash, size);
    }

    private static bool SameIdentity(string side, string left, string right)
    {
        var leftCloud = SyncAuthorityCloudObservationIdentity.TryParse(left, out var aCloud);
        var rightCloud = SyncAuthorityCloudObservationIdentity.TryParse(right, out var bCloud);
        if (leftCloud || rightCloud)
            return side == "dropbox" && leftCloud && rightCloud && aCloud is not null && bCloud is not null &&
                   aCloud.Provider == bCloud.Provider && aCloud.ProviderAccountId == bCloud.ProviderAccountId &&
                   aCloud.NamespaceId == bCloud.NamespaceId && aCloud.FileId == bCloud.FileId &&
                   aCloud.ContentAlgorithm == bCloud.ContentAlgorithm;
        using var a = JsonDocument.Parse(left); using var b = JsonDocument.Parse(right);
        return a.RootElement.GetProperty("fileId").GetString() == b.RootElement.GetProperty("fileId").GetString() &&
            (side != "local" || a.RootElement.GetProperty("volumeSerial").GetString() == b.RootElement.GetProperty("volumeSerial").GetString());
    }
    private static string? Revision(string identity)
    {
        using var document = JsonDocument.Parse(identity);
        return document.RootElement.GetProperty("revision").GetString();
    }
    private static string Id(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts)))).ToLowerInvariant()[..32];
}
