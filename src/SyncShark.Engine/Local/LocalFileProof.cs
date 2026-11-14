using System.Globalization;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Owns held-file identity and complete-content comparisons for stages and removals.</summary>
internal static class LocalFileProof
{
    // A computed content hash is the recorded one.
    internal static bool SameContent(string? computed, string? expected) => SyncContentIdentity.IdentitiesMatch(
        SyncContentIdentity.DropboxBlockSha256, computed, SyncContentIdentity.DropboxBlockSha256, expected);

    // The held bytes are the recorded bytes: the same length first, so another object is never hashed, then the
    // complete content hash.
    internal static async Task<(bool Matches, string? Hash)> SameContent(SyncLocalFileAuthority held,
        long expectedSize, string? expectedHash, CancellationToken ct)
    {
        if (held.Stream.Length != expectedSize) return (false, null);
        var hash = await LocalSide.HashAsync(held.Stream, ct).ConfigureAwait(false);
        return (SameContent(hash, expectedHash), hash);
    }

    // The held object is the recorded object with the recorded content: identity, then length (so another object is
    // never hashed), then the complete hash through the shared gate.
    internal static async Task<bool> HoldsAsync(SyncLocalFileAuthority held, StageProof proof, CancellationToken ct) =>
        Names(held.ReadIdentity(), proof) &&
        held.Stream.Length == proof.Size &&
        SameContent(await LocalSide.HashAsync(held.Stream, ct).ConfigureAwait(false), proof.ContentHash);

    internal static StageProof ProofOf(PilotDirectoryIdentity identity, string path, long size, string hash) =>
        new(path, identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
            LocalIdentity.FileIdText(identity.FileId), size, hash);

    // The held native object is the recorded one: same volume serial and file ID.
    internal static bool SameObject(PilotDirectoryIdentity identity, string volumeId, string fileId) =>
        string.Equals(identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture), volumeId,
            StringComparison.Ordinal) &&
        string.Equals(LocalIdentity.FileIdText(identity.FileId), fileId, StringComparison.Ordinal);

    // A stage proof's identity, for disposal decisions that compare no content.
    internal static bool Names(PilotDirectoryIdentity identity, StageProof proof) =>
        SameObject(identity, proof.VolumeId, proof.FileId);

}
