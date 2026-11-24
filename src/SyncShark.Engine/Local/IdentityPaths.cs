using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads local root identities and resolves a full NTFS file identity to its current path without enumerating parents or hydrating content.</summary>
public sealed class IdentityPaths
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileIdType = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    // A retired id opens as file or path not found, or as ERROR_INVALID_PARAMETER on current Windows. 87 is observed,
    // not documented (the OpenFileById reference names no error for a missing id), and a malformed descriptor or
    // handle answers it too; with documentedAbsenceOnly it counts only after a control open of the root succeeds.
    private const int ErrorInvalidParameter = 87;

    /// <summary>Reads the current location in the supplied root's native domain.</summary>
    public Task<IdentityLocation?> LocateAsync(string root,string id,CancellationToken ct) => LocateAsync(root,DriveIdentity.Read(root).Identity,id,ct);

    /// <summary>Reads an identity only in its bound domain; outside aliases are not absence.</summary>
    public Task<IdentityLocation?> LocateAsync(string root, DirectoryIdentity expectedRoot, string id, CancellationToken ct) =>
        Task.FromResult(Locate(root, expectedRoot, id, null, ct)?.Location);

    /// <summary>Prefers an eligible alias and returns its already resolved scope.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> LocateInScopeAsync(string root, DirectoryIdentity expectedRoot,
        string id, PathScope scope, CancellationToken ct) => Task.FromResult(Locate(root, expectedRoot, id, scope, ct));

    private static (IdentityLocation Location, bool InScope)? Locate(string root, DirectoryIdentity bound,
        string id, PathScope? scope, CancellationToken ct)
    {
        if (!long.TryParse(id, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
            throw new ItemFailedException(id, TextKey.ReadLocalFailed, null);
        var (full, links) = ResolveLinksById(root, bound, unchecked((ulong)number), false, ct);
        if (full is null) return null;
        string? relative = null;
        var eligible = false;
        if (links is not null)
        {
            string? best = null;
            foreach (var name in links)
            {
                ct.ThrowIfCancellationRequested();
                var path = PathNames.RelativeUnderLocalRoot(root, name);
                var included = path is not null && !(scope?.Excludes(path, null, false) ?? false);
                if (best is null || (included && !eligible) || (included == eligible && string.CompareOrdinal(name, best) < 0))
                    (best, relative, eligible) = (name, path, included);
            }
            full = best ?? throw new ChangedUnderneathException(id);
        }
        else relative = PathNames.RelativeUnderLocalRoot(root, full);
        var entry = DirectoryListing.One(Path.GetDirectoryName(full)!, Path.GetFileName(full));
        if (entry is null || entry.Id != id) throw new ChangedUnderneathException(full);
        if (links is null) eligible = relative is not null && !(scope?.Excludes(relative, null, entry.IsFolder) ?? false);
        return (new IdentityLocation(bound.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), full,
            entry with { Path = relative ?? full }), eligible);
    }
    // The legacy handle reports the low 32 bits of FILE_ID_INFO's volume serial.
    private static bool SameVolume(uint legacySerial, ulong nativeSerial) => legacySerial == unchecked((uint)nativeSerial);

    private static bool Retired(SafeFileHandle volume, DirectoryIdentity root, int openError)
    {
        if (openError is ErrorFileNotFound or ErrorPathNotFound) return true;
        return false;
    }

    // The verified volume and opened identity stay held through path and optional link resolution.
    /// <summary>Returns complete native names when forced or multiply linked; an ordinary file has no link collection.</summary>
    public static (string? Path, IReadOnlyList<string>? Links) ResolveLinksById(string rootPath,
        DirectoryIdentity expectedRoot, ulong fileId, bool forceEnumeration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(rootPath);
        var volumeRoot = Path.GetPathRoot(fullRoot);
        if (volumeRoot is null || volumeRoot.Length < 2 || volumeRoot[1] != ':')
            throw new ItemFailedException(rootPath, TextKey.RootLocalNetworkUnsupported, null);
        using var rootHandle = DriveIdentity.Open(rootPath, expectedRoot: expectedRoot);
        using var volume = OpenVolume(volumeRoot, rootPath, expectedRoot);
        using var handle = OpenById(volume, fileId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (Retired(volume, expectedRoot, error)) return (null, null);
            throw new Win32Exception(error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var information=DriveIdentity.Basic(handle);
        // NTFS keeps unlinked objects open under its deleted-object namespace until the final handle closes.
        if (information.NumberOfLinks == 0) return (null, null);
        var path = FinalPath(handle);
        if ((information.FileAttributes & FileAttributes.Directory) != 0 ||
            !forceEnumeration && information.NumberOfLinks == 1) return (path, null);
        var links = ResolveLinkNames(volume, handle, fileId, cancellationToken);
        if(links.Count != information.NumberOfLinks) throw new ChangedUnderneathException(path);
        return (path, links);
    }

    // The volume is part of root authentication, not a failed lookup of a child ID.
    private static SafeFileHandle OpenVolume(string volumeRoot, string rootPath, DirectoryIdentity expectedRoot)
    {
        SafeFileHandle? volume = null;
        try
        {
            volume = DriveIdentity.Open(volumeRoot);
            if (!SameVolume(DriveIdentity.Basic(volume).VolumeSerialNumber, expectedRoot.VolumeSerialNumber))
                throw new ChangedUnderneathException(rootPath);
            return volume;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            volume?.Dispose();
            throw new ChangedUnderneathException(rootPath, error);
        }
        catch { volume?.Dispose(); throw; }
    }

    private static IReadOnlyList<string> ResolveLinkNames(SafeFileHandle volume, SafeFileHandle handle, ulong fileId,
        CancellationToken cancellationToken)
    {
        var names = QueryLinks(handle, fileId, cancellationToken);
        var parents = new Dictionary<ulong, string>();
        var links = new List<string>(names.Count);
        foreach (var (parentId, name) in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parents.TryGetValue(parentId, out var parentPath))
            {
                using (var parent = OpenById(volume, parentId))
                {
                    if (parent.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter)
                            throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId),new Win32Exception(error));
                        throw new Win32Exception(error);
                    }
                    parentPath = FinalPath(parent);
                }
                parents.Add(parentId, parentPath);
            }
            links.Add(Path.Join(parentPath, name));
        }
        return links;
    }

    // FILE_INFORMATION_CLASS and NTSTATUS values from ntifs.h; the initial buffer holds one maximum NTFS name.
    private const int FileHardLinkInformation = 46;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int LinksHeader = 8;
    private const int LinkNameOffset = 20;
    private const int ParentIdOffset = 8;
    private const int NameLengthOffset = 16;
    // NTFS maximum name length 255.
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
                    throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
                buffer = new byte[(int)needed];
                continue;
            }
            if (status != 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
            if (io.Information > (nuint)buffer.Length) throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
            return ParseLinks(buffer.AsSpan(0, checked((int)io.Information)), fileId);
        }
    }

    internal static IReadOnlyList<(ulong Parent, string Name)> ParseLinks(ReadOnlySpan<byte> buffer, ulong fileId)
    {
        if (buffer.Length < LinksHeader || BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 0)
            throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[sizeof(uint)..]);
        if (count == 0 || count > (buffer.Length - LinksHeader) / LinkNameOffset)
            throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
        var result = new List<(ulong Parent, string Name)>((int)count);
        var offset = LinksHeader;
        for (var index = 0; index < count; index++)
        {
            if (buffer.Length - offset < LinkNameOffset) throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
            var entry = buffer[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[NameLengthOffset..]);
            if (length == 0 || length > (entry.Length - LinkNameOffset) / sizeof(char))
                throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
            var end = LinkNameOffset + (int)length * sizeof(char);
            if (index == count - 1 ? next != 0 : next < end || next % sizeof(ulong) != 0 || next > entry.Length - LinkNameOffset)
                throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
            var name = Encoding.Unicode.GetString(entry.Slice(LinkNameOffset, (int)length * sizeof(char)));
            if (name.IndexOfAny(['\\', '/', '\0']) >= 0 || name is "." or "..")
                throw new ChangedUnderneathException(LocalIdentity.FileIdText(fileId));
            result.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry[ParentIdOffset..]), name));
            if (next != 0) offset += checked((int)next);
        }
        return result;
    }

    private const int MaximumPathCharacters = 32768; // Win32 extended-length path limit 32,767 plus the terminator.

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[MaximumPathCharacters];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException(Text.Get(TextKey.RootLocalIdentityUnavailable));
        var value = new string(buffer, 0, (int)length);
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + value[8..];
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value[4..] : value;
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h,[Out] char[] chars,uint length,uint flags);

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
    private static SafeFileHandle OpenById(SafeFileHandle volume, ulong fileId)
    {
        var id = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = FileIdType, Id = fileId };
        return OpenFileByIdNative(volume, ref id, FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, FileFlagBackupSemantics | FileFlagOpenReparsePoint | DriveIdentity.OpenNoRecall);
    }

    // FILE_ID_DESCRIPTOR is 24 bytes (winbase.h: dwSize, Type, 16-byte union); the struct declares only the 8-byte id.
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
