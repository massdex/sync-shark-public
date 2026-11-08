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
            if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter) return null;
            throw new Win32Exception(error, "The file identity could not be opened.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // NTFS keeps unlinked objects open under its deleted-object namespace until the final handle closes.
        if (information.NumberOfLinks == 0) return null;
        return PilotDirectoryLease.FinalPath(handle);
    }

    /// <summary>Opens a file or directory by identity without following reparse points or hydrating content.
    /// The caller owns the returned handle and interprets the native error when it is invalid.</summary>
    public static SafeFileHandle OpenById(SafeFileHandle volume, ulong fileId) =>
        OpenById(volume, fileId, FileReadAttributes, FileFlagBackupSemantics | FileFlagOpenReparsePoint);

    internal static SafeFileHandle OpenById(SafeFileHandle volume, ulong fileId, uint access, uint flags)
    {
        var id = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = FileIdType, Id = fileId };
        return OpenFileByIdNative(volume, ref id, access,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, flags);
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
