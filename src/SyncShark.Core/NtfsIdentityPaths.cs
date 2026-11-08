using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

/// <summary>Reads local root identities and resolves a full NTFS file identity to its current path without enumerating parents or hydrating content.</summary>
public static class NtfsIdentityPaths
{
    private const uint FileReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileIdType = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    // OpenFileById reports a retired NTFS file id as ERROR_INVALID_PARAMETER on current Windows; the
    // descriptor and volume handle are this method's own fixed inputs, so 87 here means the id is absent.
    private const int ErrorInvalidParameter = 87;

    /// <summary>Reads the configured directory's volume and file identity through the shared directory reader.</summary>
    public static DirectoryIdentity ReadRootIdentity(string directoryPath) =>
        WindowsDirectoryListing.ReadIdentity(directoryPath);

    /// <summary>Compares a legacy NTFS identity with the native FILE_ID_INFO representation.</summary>
    public static bool MatchesLegacyIdentity(DirectoryIdentity observed, uint volumeSerial, ulong fileId) =>
        SameVolume(volumeSerial, observed.VolumeSerialNumber) && observed.FileId == fileId &&
        observed.FileIdHigh is null or 0;

    // The legacy handle reports the low 32 bits of FILE_ID_INFO's volume serial.
    private static bool SameVolume(uint legacySerial, ulong nativeSerial) => legacySerial == unchecked((uint)nativeSerial);

    /// <summary>
    /// Resolves a full NTFS file identity to its current path, or null when that identity no longer exists.
    /// A location outside <paramref name="rootPath"/> is a location, never an absence.
    /// </summary>
    public static string? ResolvePathById(string rootPath, DirectoryIdentity expectedRoot, ulong fileId,
        CancellationToken cancellationToken) =>
        Resolve(rootPath, expectedRoot, fileId, enumerateLinks: false, forceEnumeration: false, cancellationToken).Path;

    /// <summary>Returns complete native names when forced or multiply linked; an ordinary file has no link collection.</summary>
    public static (string? Path, IReadOnlyList<string>? Links) ResolveLinksById(string rootPath,
        DirectoryIdentity expectedRoot, ulong fileId, bool forceEnumeration, CancellationToken cancellationToken) =>
        Resolve(rootPath, expectedRoot, fileId, enumerateLinks: true, forceEnumeration, cancellationToken);

    private static (string? Path, IReadOnlyList<string>? Links) Resolve(string rootPath,
        DirectoryIdentity expectedRoot, ulong fileId, bool enumerateLinks, bool forceEnumeration,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(rootPath);
        var volumeRoot = Path.GetPathRoot(fullRoot);
        if (volumeRoot is null || volumeRoot.Length < 2 || volumeRoot[1] != ':')
            throw new IOException("The configured root is not on a local drive.");
        using var volume = PilotDirectoryLease.CreateFile(volumeRoot, FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The root volume could not be opened.");
        // A file identity is meaningful only on its own volume; a differing serial means the configured root moved.
        if (!PilotDirectoryLease.GetFileInformationByHandle(volume, out var volumeInformation))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The root volume identity could not be read.");
        if (!SameVolume(volumeInformation.VolumeSerialNumber, expectedRoot.VolumeSerialNumber))
            throw new IOException("The configured root volume changed.");

        using var handle = OpenById(volume, fileId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            // A retired id is reported as either form; 87 is the observed current-Windows answer.
            if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter) return (null, null);
            throw new Win32Exception(error, "The file identity could not be opened.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // NTFS keeps unlinked objects open under its deleted-object namespace until the final handle closes.
        if (information.NumberOfLinks == 0) return (null, null);
        var path = PilotDirectoryLease.FinalPath(handle);
        if (!enumerateLinks || (information.FileAttributes & FileAttributes.Directory) != 0 ||
            !forceEnumeration && information.NumberOfLinks == 1) return (path, null);
        var links = EnumerateLinks(path, volumeRoot, fileId, cancellationToken);
        if (!string.Equals(path, PilotDirectoryLease.FinalPath(handle), StringComparison.Ordinal))
            throw new NtfsLinkEnumerationChangedException(fileId);
        return (path, links);
    }

    // Win32 FindFirst/NextFileNameW return the required character count on ERROR_MORE_DATA and finish at EOF.
    private const int ErrorMoreData = 234;
    private const int ErrorHandleEof = 38;

    private static IReadOnlyList<string> EnumerateLinks(string path, string volumeRoot, ulong fileId,
        CancellationToken ct)
    {
        var buffer = new char[path.Length + 1];
        var search = new IntPtr(-1);
        var links = new List<string>();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var length = checked((uint)buffer.Length);
                Array.Clear(buffer);
                var first = search == new IntPtr(-1);
                bool found;
                if (first)
                {
                    search = FindFirstFileName(path, 0, ref length, buffer);
                    found = search != new IntPtr(-1);
                }
                else found = FindNextFileName(search, ref length, buffer);
                if (!found)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorMoreData && length > buffer.Length)
                    {
                        buffer = new char[checked((int)length)];
                        continue;
                    }
                    if (!first && error == ErrorHandleEof) return links;
                    if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorMoreData)
                        throw new NtfsLinkEnumerationChangedException(fileId, new Win32Exception(error));
                    throw new Win32Exception(error);
                }
                var end = Array.IndexOf(buffer, '\0');
                if (length > buffer.Length || end <= 0 || buffer[0] != '\\')
                    throw new NtfsLinkEnumerationChangedException(fileId);
                links.Add(Path.Join(volumeRoot, new string(buffer, 1, end - 1)));
            }
        }
        finally
        {
            if (search != new IntPtr(-1)) FindClose(search);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileName(string path, uint flags, ref uint length, [Out] char[] name);

    [DllImport("kernel32.dll", EntryPoint = "FindNextFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileName(IntPtr search, ref uint length, [Out] char[] name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr search);

    // FILE_FLAG_BACKUP_SEMANTICS lets the same call resolve a directory; OPEN_REPARSE_POINT returns the reparse
    // point itself and never follows a link or hydrates a cloud placeholder.
    private static SafeFileHandle OpenById(SafeFileHandle volume, ulong fileId)
    {
        var id = new FileIdDescriptor { Size = 24, Type = FileIdType, Id = fileId };
        return OpenFileByIdNative(volume, ref id, FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public ulong Id;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenFileById", SetLastError = true)]
    private static extern SafeFileHandle OpenFileByIdNative(SafeFileHandle volumeHint, ref FileIdDescriptor fileId,
        uint access, FileShare share, IntPtr security, uint flags);
}
