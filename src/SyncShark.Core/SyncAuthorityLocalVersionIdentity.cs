using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

internal static class SyncAuthorityLocalVersionIdentity
{
    public static string Serialize(PilotEntryKind kind, uint volumeSerial, ulong fileId,
        DateTime lastWriteUtc, string? dropboxId = null, string? dropboxRevision = null,
        string? contentAlgorithm = null, string? contentHash = null, long? size = null)
    {
        var identity = SerializeCore(kind, volumeSerial, fileId, lastWriteUtc, dropboxId, dropboxRevision);
        // The algorithm stamp retains its existing provider meaning. Legacy
        // stored identity bytes are never rewritten; new byte versions also
        // carry verified content evidence below.
        if (string.IsNullOrWhiteSpace(contentAlgorithm) && contentHash is null) return identity;
        var node = JsonNode.Parse(identity)?.AsObject()
                   ?? throw new InvalidDataException("The local version identity could not be stamped.");
        if (!string.IsNullOrWhiteSpace(contentAlgorithm)) node["contentAlgorithm"] = contentAlgorithm;
        // A physical metadata tuple is not a byte-version identity: an editor can
        // preserve both length and last-write time. Include the independently
        // verified content evidence so the existing unique version key can retain
        // both versions, without changing or rewriting any legacy database row.
        if (kind == PilotEntryKind.File && contentHash is not null)
        {
            if (size is null or < 0) throw new InvalidDataException("A byte-version identity requires its verified size.");
            node["contentHash"] = contentHash;
            node["sizeBytes"] = size.Value;
        }
        return node.ToJsonString();
    }

    private static string SerializeCore(PilotEntryKind kind, uint volumeSerial, ulong fileId,
        DateTime lastWriteUtc, string? dropboxId, string? dropboxRevision)
    {
        if (kind == PilotEntryKind.Directory)
            return JsonSerializer.Serialize(new
            {
                volumeSerial = volumeSerial.ToString("x8", CultureInfo.InvariantCulture),
                fileId = fileId.ToString("x16", CultureInfo.InvariantCulture)
            });

        if (dropboxId is not null && dropboxRevision is not null)
            return JsonSerializer.Serialize(new
            {
                volumeSerial = volumeSerial.ToString("x8", CultureInfo.InvariantCulture),
                fileId = fileId.ToString("x16", CultureInfo.InvariantCulture),
                lastWriteUtc = lastWriteUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                cloudFiles = new
                {
                    marker = WindowsCloudFileIdentity.CurrentMarker,
                    DropboxId = dropboxId,
                    DropboxRevision = dropboxRevision
                }
            });

        // Partially retained Cloud Files metadata is not sufficient authority to bind
        // a Dropbox object. It therefore has the same local identity as an ordinary file.
        return JsonSerializer.Serialize(new
        {
            volumeSerial = volumeSerial.ToString("x8", CultureInfo.InvariantCulture),
            fileId = fileId.ToString("x16", CultureInfo.InvariantCulture),
            lastWriteUtc = lastWriteUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
    }
    // Provider-scoped identity writer: every identity writer in a pair run uses this so the observed
    // identity and the post-effect identity agree, and a retained identity binds the algorithm it was
    // produced with.
    public static string SerializeFor(SyncPair pair, PilotEntryKind kind, uint volumeSerial, ulong fileId,
        DateTime lastWriteUtc, string? cloudId = null, string? cloudRevision = null,
        string? contentHash = null, long? size = null) =>
        Serialize(kind, volumeSerial, fileId, lastWriteUtc, cloudId, cloudRevision,
            SyncContentIdentity.IdentityStampFor(pair?.Provider), contentHash, size);

    // Only the new redundant byte-evidence fields may differ from a legacy
    // identity. Every physical/provider field remains exact, and any declared
    // byte evidence must match the separately verified version columns.
    public static bool EquivalentWithKnownContent(string left, string right, string? contentHash, long? size)
    {
        if (contentHash is null || size is null)
            return string.Equals(left, right, StringComparison.Ordinal);
        var leftNode = JsonNode.Parse(left)?.AsObject();
        var rightNode = JsonNode.Parse(right)?.AsObject();
        if (leftNode is null || rightNode is null) return false;
        bool RemoveVerifiedEvidence(JsonObject node)
        {
            if (!node.ContainsKey("contentHash") && !node.ContainsKey("sizeBytes")) return true;
            if (node["contentHash"] is not JsonValue hashNode || !hashNode.TryGetValue<string>(out var hash) ||
                !string.Equals(hash, contentHash, StringComparison.OrdinalIgnoreCase) ||
                node["sizeBytes"] is not JsonValue sizeNode || !sizeNode.TryGetValue<long>(out var length) ||
                length != size.Value) return false;
            node.Remove("contentHash");
            node.Remove("sizeBytes");
            return true;
        }
        return RemoveVerifiedEvidence(leftNode) && RemoveVerifiedEvidence(rightNode) &&
               string.Equals(leftNode.ToJsonString(), rightNode.ToJsonString(), StringComparison.Ordinal);
    }

    // A retained identity produced with a different content-identity algorithm is refused rather than
    // reinterpreted. A missing stamp is the legacy Dropbox scheme, which only matches a Dropbox pair.
    public static void RequireIdentityAlgorithm(string? identityJson, string? provider)
    {
        if (string.IsNullOrWhiteSpace(identityJson)) return;
        using var document = JsonDocument.Parse(identityJson);
        var retained = document.RootElement.TryGetProperty("contentAlgorithm", out var node)
            ? node.GetString()
            : null;
        var current = SyncContentIdentity.IdentityStampFor(provider);
        if (string.Equals(retained, current, StringComparison.Ordinal)) return;
        throw new GoogleDriveBlockedException(
            "The retained local identity was produced with a different content-identity algorithm (" +
            (retained ?? SyncContentIdentity.DropboxBlockSha256) + ") than this pair uses (" +
            (current ?? SyncContentIdentity.DropboxBlockSha256) +
            "). The operation was refused rather than reinterpreting retained state.");
    }
}
