namespace Massdex.SyncShark.Engine;

/// <summary>Authoritative proof of the exact losing file the Executor selected for local removal.
/// VolumeId and ContentHash describe those selected bytes, never the incoming stage; a folder has no
/// file-hash removal proof and stays with the folder-removal owner.
/// Local identity text is invariant decimal: Expected.Id exactly as LocalListing wrote it and VolumeId from the
/// volume serial. LocalBin compares both ordinally with the held file, so another spelling fails closed.</summary>
public sealed record RemovalProof(Entry Expected, string VolumeId, string ContentHash)
{
    public Entry Expected { get; init; } = RequireEntry(Expected);
    public string VolumeId { get; init; } = RequireId(VolumeId, nameof(VolumeId));
    public string ContentHash { get; init; } = RequireId(ContentHash, nameof(ContentHash));

    private static Entry RequireEntry(Entry expected)
    {
        if (expected is null) throw new ArgumentNullException(nameof(Expected));
        if (expected.IsFolder || string.IsNullOrWhiteSpace(expected.Id))
            throw new ArgumentException(null, nameof(Expected));
        return expected;
    }

    private static string RequireId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(null, name);
        return value;
    }
}
