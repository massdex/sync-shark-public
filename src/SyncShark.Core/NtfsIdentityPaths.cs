using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
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

    // B0 precedes the held-object query; B1 follows it; B2 brackets each distinct parent observation.
    internal static (string? Path, IReadOnlyList<string>? Links) ResolveLinksById(string rootPath,
        DirectoryIdentity expectedRoot, ulong fileId, bool forceEnumeration, CancellationToken cancellationToken,
        Action? beforeQuery, Action? afterQuery, Action<ulong, bool>? parentObservation) =>
        Resolve(rootPath, expectedRoot, fileId, true, forceEnumeration, cancellationToken,
            beforeQuery, afterQuery, parentObservation);

    private static (string? Path, IReadOnlyList<string>? Links) Resolve(string rootPath,
        DirectoryIdentity expectedRoot, ulong fileId, bool enumerateLinks, bool forceEnumeration,
        CancellationToken cancellationToken, Action? beforeQuery = null, Action? afterQuery = null,
        Action<ulong, bool>? parentObservation = null)
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
        beforeQuery?.Invoke();
        var names = QueryLinks(handle, fileId, cancellationToken);
        afterQuery?.Invoke();
        var parents = new Dictionary<ulong, string>();
        var links = new List<string>(names.Count);
        foreach (var (parentId, name) in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parents.TryGetValue(parentId, out var parentPath))
            {
                parentObservation?.Invoke(parentId, false);
                using (var parent = OpenById(volume, parentId))
                {
                    if (parent.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter)
                            throw new NtfsLinkEnumerationChangedException(fileId, new Win32Exception(error));
                        throw new Win32Exception(error);
                    }
                    parentPath = PilotDirectoryLease.FinalPath(parent);
                }
                parents.Add(parentId, parentPath);
                parentObservation?.Invoke(parentId, true);
            }
            links.Add(Path.Join(parentPath, name));
        }
        return (path, links);
    }

    // FILE_INFORMATION_CLASS and NTSTATUS values from ntifs.h; the initial buffer holds one maximum NTFS name.
    private const int FileHardLinkInformation = 46;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int LinksHeader = 8;
    private const int LinkNameOffset = 20;
    private const int ParentIdOffset = 8;
    private const int NameLengthOffset = 16;
    private const int MaximumNameCharacters = 255;

    private static IReadOnlyList<(ulong Parent, string Name)> QueryLinks(SafeFileHandle handle, ulong fileId,
        CancellationToken ct)
    {
        var buffer = new byte[LinksHeader + LinkNameOffset + MaximumNameCharacters * sizeof(char)];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = NtQueryInformationFile(handle, out var io, buffer, checked((uint)buffer.Length), FileHardLinkInformation);
            if (status == StatusBufferOverflow)
            {
                var needed = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                if (needed <= buffer.Length || needed > int.MaxValue)
                    throw new NtfsLinkEnumerationChangedException(fileId);
                buffer = new byte[(int)needed];
                continue;
            }
            if (status != 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
            if (io.Information > (nuint)buffer.Length) throw new NtfsLinkEnumerationChangedException(fileId);
            return ParseLinks(buffer.AsSpan(0, checked((int)io.Information)), fileId);
        }
    }

    internal static IReadOnlyList<(ulong Parent, string Name)> ParseLinks(ReadOnlySpan<byte> buffer, ulong fileId)
    {
        if (buffer.Length < LinksHeader || BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 0)
            throw new NtfsLinkEnumerationChangedException(fileId);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[sizeof(uint)..]);
        if (count == 0 || count > (buffer.Length - LinksHeader) / LinkNameOffset)
            throw new NtfsLinkEnumerationChangedException(fileId);
        var result = new List<(ulong Parent, string Name)>((int)count);
        var offset = LinksHeader;
        for (var index = 0; index < count; index++)
        {
            if (buffer.Length - offset < LinkNameOffset) throw new NtfsLinkEnumerationChangedException(fileId);
            var entry = buffer[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[NameLengthOffset..]);
            if (length == 0 || length > (entry.Length - LinkNameOffset) / sizeof(char))
                throw new NtfsLinkEnumerationChangedException(fileId);
            var end = LinkNameOffset + (int)length * sizeof(char);
            if (index == count - 1 ? next != 0 : next < end || next % sizeof(ulong) != 0 || next > entry.Length - LinkNameOffset)
                throw new NtfsLinkEnumerationChangedException(fileId);
            var name = Encoding.Unicode.GetString(entry.Slice(LinkNameOffset, (int)length * sizeof(char)));
            if (name.IndexOfAny(['\\', '/', '\0']) >= 0 || name is "." or "..")
                throw new NtfsLinkEnumerationChangedException(fileId);
            result.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry[ParentIdOffset..]), name));
            if (next != 0) offset += checked((int)next);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle handle, out IoStatusBlock io,
        [Out] byte[] buffer, uint length, int informationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

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
