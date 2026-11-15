namespace Massdex.SyncShark.V2;

/// <summary>NTFS name and USN record sizes shared by native readers and configuration validation; no native I/O.</summary>
internal static class NtfsRecordLayout
{
    // Windows file-system limits: NTFS components contain at most 255 UTF-16 code units.
    // https://learn.microsoft.com/windows/win32/fileio/filesystem-functionality-comparison
    internal const int MaximumNameCharacters = 255;
    // Windows SDK winioctl.h USN_RECORD_V2: FileName offset 60, records aligned on 64-bit boundaries.
    // https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-usn_record_v2
    internal const int UsnHeaderBytes = 60;
    private const int AlignmentBytes = sizeof(ulong);
    internal const int MaximumUsnRecordBytes =
        (UsnHeaderBytes + MaximumNameCharacters * sizeof(char) + AlignmentBytes - 1) / AlignmentBytes * AlignmentBytes;
}
