namespace Massdex.SyncShark;

/// <summary>Identifies incomplete link information or a retired parent during reconstruction, never identity absence.</summary>
public sealed class NtfsLinkEnumerationChangedException(ulong fileId, Exception? inner = null)
    : IOException(nameof(NtfsLinkEnumerationChangedException), inner)
{
    public ulong FileId { get; } = fileId;
}
