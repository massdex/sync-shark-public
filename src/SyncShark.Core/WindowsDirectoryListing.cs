using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private const int FileIdBothDirectoryInformation = 37;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNoMoreFiles = 18;
    // Documented GetFileInformationByHandleEx results for an information class the file system does not support.
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorCallNotImplemented = 120;
    private const int ErrorInvalidLevel = 124;
    private const int FileIdInfo = 18;
    private const int BufferSize = 64 * 1024;
    private const string NativeLibrary = "ntdll.dll";
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
    private const int MaxNameCharacters = V2.NtfsRecordLayout.MaximumNameCharacters;
    private const int NamedBufferSize = FileNameOffset + MaxNameCharacters * sizeof(char);

    public static IEnumerable<DirectoryEntryInfo> List(string directoryPath) =>
        List(directoryPath, (PilotDirectoryIdentity?)null);

    public static IEnumerable<DirectoryEntryInfo> List(string directoryPath, PilotDirectoryIdentity? expectedIdentity)
    {
        using var handle = OpenDirectory(directoryPath);
        // The identity proof and enumeration must use this same handle, never a check-then-reopen.
        if (expectedIdentity is not null)
        {
            if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var identity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The directory identity could not be read.");
            if (identity.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber ||
                (((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow) != expectedIdentity.FileId ||
                (identity.FileAttributes & FileAttributes.Directory) == 0)
                throw new DirectoryIdentityMismatchException();
        }
        foreach (var item in Enumerate(handle)) yield return item;
    }

    /// <summary>Enumerates a directory, reporting the id of the handle actually enumerated before the first entry;
    /// the box outlives the handle, so a caller can read it after disposal.</summary>
    public static IEnumerable<DirectoryEntryInfo> List(string directoryPath, StrongBox<long> openedId)
    {
        using var handle = OpenDirectory(directoryPath);
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var identity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The directory identity could not be read.");
        openedId.Value = unchecked((long)(((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow));
        foreach (var item in Enumerate(handle)) yield return item;
    }

    /// <summary>Reads the directory's volume and file identity from its own held handle, preferring full-width FileIdInfo.</summary>
    public static DirectoryIdentity ReadIdentity(string directoryPath)
    {
        using var handle = OpenDirectory(directoryPath);
        return ReadIdentity(handle);
    }

    /// <summary>Enumerates a root, requiring the identity of its own enumeration handle to equal expectedIdentity.</summary>
    public static IEnumerable<DirectoryEntryInfo> ListRoot(string directoryPath, DirectoryIdentity expectedIdentity)
    {
        using var handle = OpenDirectory(directoryPath);
        if (ReadIdentity(handle) != expectedIdentity) throw new DirectoryIdentityMismatchException();
        foreach (var item in Enumerate(handle)) yield return item;
    }

    // The shared native identity read: classify the held root first, then prefer the full-width FileIdInfo form and
    // fall back to the basic form only for a documented unsupported information class.
    private static DirectoryIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var basic))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The directory identity could not be read.");
        // The shared supported-root admission, applied to this same held handle: an unsafe object is accepted only
        // through an existing cloud-root exception, never by reopening its path.
        PilotPath.RequireSupportedDirectoryRoot(handle, basic.FileAttributes);

        if (GetFileInformationByHandleEx(handle, FileIdInfo, out var wide, (uint)Marshal.SizeOf<FileIdInfoBuffer>()))
            return new DirectoryIdentity(wide.VolumeSerialNumber, wide.FileIdLow, wide.FileIdHigh);
        var error = Marshal.GetLastWin32Error();
        if (error is not (ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter or ErrorCallNotImplemented or ErrorInvalidLevel))
            throw new Win32Exception(error, "The directory identity could not be read.");
        return new DirectoryIdentity(basic.VolumeSerialNumber,
            ((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow, null);
    }

    private static IEnumerable<DirectoryEntryInfo> Enumerate(SafeFileHandle handle)
    {
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
                    var item = Decode(buffer + offset);
                    if (item is not null) yield return item;
                    if (nextOffset == 0) break;
                    offset += nextOffset;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Looks up one name without opening the child; returns null if no matching entry exists,
    /// including when the opened parent is proven to be an ordinary file.</summary>
    public static DirectoryEntryInfo? Named(string directoryPath, string name)
    {
        if (name.Length == 0 || name.Length > MaxNameCharacters) return null;
        using var handle = OpenDirectory(directoryPath);
        var filter = new UnicodeString
        {
            Length = checked((ushort)(name.Length * sizeof(char))),
            MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
            Buffer = name
        };
        var buffer = Marshal.AllocHGlobal(NamedBufferSize);
        try
        {
            // shortcut: case-sensitive directories can reject differently cased names; extend when these roots are supported.
            var status = NtQueryDirectoryFile(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _,
                buffer, NamedBufferSize, FileIdBothDirectoryInformation, true, ref filter, true);
            if (status < 0)
            {
                var error = (int)RtlNtStatusToDosError(status);
                if (error is ErrorFileNotFound or ErrorNoMoreFiles) return null;
                // A replaced parent proves absence only on this handle, never by reopening its path.
                if (error == ErrorInvalidParameter &&
                    PilotDirectoryLease.GetFileInformationByHandle(handle, out var basic) &&
                    (basic.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                    return null;
                throw new Win32Exception(error);
            }
            var item = Decode(buffer);
            // Reject DOS aliases; callers match the long name, as the listing does.
            // shortcut: volume upcase tables can disagree with OrdinalIgnoreCase; reconcile when supporting those casing rules.
            return item is not null && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) ? item : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle OpenDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = PilotDirectoryLease.CreateFile(directoryPath,
            FileListDirectory | FileReadAttributes, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "The directory could not be opened for metadata enumeration.");
    }

    private static DirectoryEntryInfo? Decode(IntPtr record)
    {
        var nameLength = Marshal.ReadInt32(record, FileNameLengthOffset);
        if (nameLength <= 0 || nameLength > MaxNameCharacters * sizeof(char)) return null;
        var name = Marshal.PtrToStringUni(record + FileNameOffset, nameLength / sizeof(char));
        if (name is null or "." or "..") return null;
        var attributes = (FileAttributes)(uint)Marshal.ReadInt32(record, FileAttributesOffset);
        return new DirectoryEntryInfo(name, Marshal.ReadInt64(record, FileIdOffset),
            Marshal.ReadInt64(record, EndOfFileOffset),
            DateTime.FromFileTimeUtc(Marshal.ReadInt64(record, LastWriteTimeOffset)), attributes,
            (attributes & FileAttributes.ReparsePoint) != 0 ? (uint)Marshal.ReadInt32(record, EaSizeOffset) : 0);
    }

    internal sealed class DirectoryIdentityMismatchException() : IOException("replaced during scan");

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)] public string Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    // FILE_ID_INFO: a 64-bit volume serial and a 128-bit file id (two unsigned 64-bit halves).
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoBuffer
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport(NativeLibrary)]
    private static extern int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr @event, IntPtr apcRoutine,
        IntPtr apcContext, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length,
        int fileInformationClass, [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        ref UnicodeString fileName, [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport(NativeLibrary)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        IntPtr lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        out FileIdInfoBuffer lpFileInformation, uint dwBufferSize);
}
