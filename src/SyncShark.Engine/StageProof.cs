namespace Massdex.SyncShark.Engine;

/// <summary>The verified closed set of values identifying staged content. Never a bin receipt.</summary>
public sealed record StageProof
{
    public string Path { get; }
    public string VolumeId { get; }
    public string FileId { get; }
    public long Size { get; }
    public string ContentHash { get; }
    public StageProof(string path, string volumeId, string fileId, long size, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId); ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (!global::Massdex.SyncShark.Engine.ContentHash.IsValid(contentHash ?? string.Empty)) throw new ArgumentException(nameof(contentHash));
        Path=path; VolumeId=volumeId; FileId=fileId; Size=size; ContentHash=contentHash!;
    }
}
