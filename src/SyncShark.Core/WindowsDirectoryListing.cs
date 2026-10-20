using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

/// <summary>One listed entry; ReparseTag is the entry's reparse tag when it carries FILE_ATTRIBUTE_REPARSE_POINT, else 0.</summary>
public sealed record DirectoryEntryInfo(string Name, long FileId, long Size,
    DateTime LastWriteTimeUtc, FileAttributes Attributes, uint ReparseTag = 0);

// Enumerates a directory's entries with their NTFS identity from a single FileIdBothDirectoryInfo
// enumeration on one directory handle; ordinary files and subdirectories are never opened individually.
public static class WindowsDirectoryListing
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileIdBothDirectoryInfo = 10;
    private const int ErrorNoMoreFiles = 18;
    private const int BufferSize = 64 * 1024;
    // FILE_ID_BOTH_DIR_INFO offsets (Windows SDK default packing; identical on x86/x64/ARM64):
    // NextEntryOffset 0, LastWriteTime 24, EndOfFile 40, FileAttributes 56, FileNameLength 60, EaSize 64,
    // ShortNameLength 68, ShortName[12] 70, FileId 96, FileName 104. For a reparse point the file system reports the
    // reparse tag in EaSize (the same value FindFirstFile returns in dwReserved0), so no entry is ever opened for it.
    private const int NextEntryOffsetOffset = 0;
    private const int LastWriteTimeOffset = 24;
    private const int EndOfFileOffset = 40;
    private const int FileAttributesOffset = 56;
    private const int FileNameLengthOffset = 60;
    private const int EaSizeOffset = 64;
    private const int FileIdOffset = 96;
    private const int FileNameOffset = 104;

    public static IEnumerable<DirectoryEntryInfo> List(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var handle = PilotDirectoryLease.CreateFile(directoryPath,
            FileListDirectory | FileReadAttributes, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The directory could not be opened for metadata enumeration.");
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(handle, FileIdBothDirectoryInfo, buffer, BufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles) yield break;
                    throw new Win32Exception(error, "The directory metadata could not be enumerated.");
                }
                var offset = 0;
                while (true)
                {
                    var nextOffset = Marshal.ReadInt32(buffer, offset + NextEntryOffsetOffset);
                    var nameLength = (int)Marshal.ReadInt32(buffer, offset + FileNameLengthOffset);
                    // A single Windows file name is at most 255 UTF-16 code units (510 bytes).
                    if (nameLength > 0 && nameLength <= 2 * 255)
                    {
                        var name = Marshal.PtrToStringUni(buffer + offset + FileNameOffset, nameLength / 2);
                        var attributes = (FileAttributes)(uint)Marshal.ReadInt32(buffer, offset + FileAttributesOffset);
                        if (name is not null && name != "." && name != "..")
                            yield return new DirectoryEntryInfo(name,
                                Marshal.ReadInt64(buffer, offset + FileIdOffset),
                                Marshal.ReadInt64(buffer, offset + EndOfFileOffset),
                                DateTime.FromFileTimeUtc(Marshal.ReadInt64(buffer, offset + LastWriteTimeOffset)),
                                attributes, (attributes & FileAttributes.ReparsePoint) != 0
                                    ? (uint)Marshal.ReadInt32(buffer, offset + EaSizeOffset)
                                    : 0);
                    }
                    if (nextOffset == 0) break;
                    offset += nextOffset;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        IntPtr lpFileInformation, uint dwBufferSize);
}
