namespace Massdex.SyncShark;

/// <summary>Metadata-only evidence from one open NTFS file, never content or a path-time guess.</summary>
internal sealed record NtfsFileUsn(uint Volume, ulong FileId, long Usn, long Size, long MtimeFileTime)
{
    // Cloud-provider metadata is not sufficient evidence of unchanged local contents.
    public bool RequiresHash { get; init; }
}
