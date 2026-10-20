using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

/// <summary>Metadata-only evidence from one open NTFS file, never content or a path-time guess.</summary>
internal sealed record NtfsFileUsn(uint Volume, ulong FileId, long Usn, long Size, long MtimeFileTime);

public static partial class NtfsUsnJournal
{
    private const uint FsctlReadFileUsnData = 0x000900eb;

    internal static bool TryReadFileUsn(string path, out NtfsFileUsn? evidence)
    {
        evidence = null;
        if (!OperatingSystem.IsWindows()) return false;
        // OPEN_REPARSE_POINT prevents following a final-component link or hydrating a stub.
        // Ancestor/root identity validation remains the caller's guarded-root responsibility.
        // Never deny user saves. Later writes/close records remain in the live journal;
        // callers must revalidate staged content rather than treating this as a lock.
        using var handle = OpenGapMetadata(path);
        return !handle.IsInvalid && TryReadFileUsn(handle, out evidence);
    }

    internal static SafeFileHandle OpenGapVolume(string path) =>
        CreateFile(Path.GetPathRoot(Path.GetFullPath(path))!, 0x80,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, OpenExisting, 0x02000000, IntPtr.Zero);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 24)]
    private struct GapFileId
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Size;
        [System.Runtime.InteropServices.FieldOffset(4)] public uint Type;
        [System.Runtime.InteropServices.FieldOffset(8)] public ulong Id;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref GapFileId id,
        uint access, FileShare share, IntPtr security, uint flags);

    internal static SafeFileHandle OpenGapMetadata(SafeFileHandle volume, ulong fileId)
    {
        var id = new GapFileId { Size = 24, Type = 0, Id = fileId };
        return OpenFileById(volume, ref id, 0x80, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, 0x00200000);
    }

    internal static SafeFileHandle OpenGapMetadata(string path) =>
        CreateFile(path, 0x80, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0x00200000, IntPtr.Zero);

    internal static SafeFileHandle OpenGapContent(SafeFileHandle volume, ulong fileId)
    {
        var id = new GapFileId { Size = 24, Type = 0, Id = fileId };
        var handle = OpenFileById(volume, ref id, GenericRead, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, 0x00200000 | 0x08000000);
        if (!handle.IsInvalid)
        {
            var priority = 0;
            _ = SetFileInformationByHandle(handle, 12, ref priority, sizeof(int));
        }
        return handle;
    }

    internal static SafeFileHandle OpenGapContent(string path)
    {
        var handle = CreateFile(path, GenericRead, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0x00200000 | 0x08000000, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            var priority = 0; // IoPriorityVeryLow; best effort on filesystems that support it.
            _ = SetFileInformationByHandle(handle, 12, ref priority, sizeof(int));
        }
        return handle;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int value, int size);

    internal static bool TryReadFileUsn(SafeFileHandle handle, out NtfsFileUsn? evidence)
    {
        evidence = null;
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var before) ||
            (before.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                FileAttributes.Offline | (FileAttributes)0x00400000 | (FileAttributes)0x00040000)) != 0)
            return false;
        var bytes = new byte[4096];
        if (!DeviceIoControlBytes(handle, FsctlReadFileUsnData, null, 0, bytes, bytes.Length,
                out var count, IntPtr.Zero) || count < 60 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) > count ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) < 60 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 2)
            return false; // Unknown/unsupported record formats are never evidence of unchanged content.
        var id = ((ulong)before.FileIndexHigh << 32) | before.FileIndexLow;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24));
        if (usn < 0 || BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) != id ||
            !PilotDirectoryLease.GetFileInformationByHandle(handle, out var after) ||
            after.VolumeSerialNumber != before.VolumeSerialNumber ||
            after.FileIndexHigh != before.FileIndexHigh || after.FileIndexLow != before.FileIndexLow ||
            after.FileSizeHigh != before.FileSizeHigh || after.FileSizeLow != before.FileSizeLow ||
            after.LastWriteTime.dwHighDateTime != before.LastWriteTime.dwHighDateTime ||
            after.LastWriteTime.dwLowDateTime != before.LastWriteTime.dwLowDateTime)
            return false;
        evidence = new(before.VolumeSerialNumber, id, usn,
            checked((long)(((ulong)before.FileSizeHigh << 32) | before.FileSizeLow)),
            ((long)before.LastWriteTime.dwHighDateTime << 32) | (uint)before.LastWriteTime.dwLowDateTime);
        return true;
    }
}
