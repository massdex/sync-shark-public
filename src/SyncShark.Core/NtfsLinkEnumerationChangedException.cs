namespace Massdex.SyncShark;

/// <summary>Identifies an incomplete or changed pathname-based link enumeration, never identity absence.</summary>
public sealed class NtfsLinkEnumerationChangedException(ulong fileId, Exception? inner = null)
    : IOException(nameof(NtfsLinkEnumerationChangedException), inner)
{
    public ulong FileId { get; } = fileId;
}
