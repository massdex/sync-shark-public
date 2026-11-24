namespace Massdex.SyncShark.Engine;

/// <summary>Authoritative proof of the exact losing entry the Executor selected for local removal.
/// VolumeId and ContentHash describe those selected bytes, never the incoming stage; a folder carries its selected identity and binding, not a file digest.
/// Local identity text is invariant decimal: Expected.Id exactly as LocalListing wrote it and VolumeId from the
/// volume serial. LocalBin compares both ordinally with the held file, so another spelling fails closed.</summary>
public sealed record RemovalProof
{
    public Entry Expected { get; }
    public string VolumeId { get; }
    public string? ContentHash { get; }
    public RemovalProof(Entry expected, string volumeId, string? contentHash)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        // Local revalidates the selected placeholder's identity, metadata and still-dehydrated state on the held handle.
        bool metadataOnly = expected.IsFolder || expected.Skip == nameof(TextKey.SkipOnlineOnly);
        if (expected.Size < 0 || (metadataOnly ? contentHash is not null : !global::Massdex.SyncShark.Engine.ContentHash.IsValid(contentHash ?? string.Empty)))
            throw new ArgumentException(nameof(contentHash));
        Expected = expected; VolumeId = volumeId; ContentHash = contentHash;
    }
}
