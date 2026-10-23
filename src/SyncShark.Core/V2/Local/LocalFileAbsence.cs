using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

internal enum FileAbsence { Unknown, Present, Gone }

/// <summary>One identity-verification scope; an unavailable root never proves absence.</summary>
internal sealed class LocalFileAbsence : IDisposable
{
    private readonly PilotDirectoryLease? _root;
    private readonly string? _rootPath;
    private readonly char[] _pathBuffer = new char[32768];

    internal LocalFileAbsence(string rootPath, PilotDirectoryIdentity rootIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _root = PilotDirectoryLease.Open(rootPath, rootIdentity);
            if (FinalPath(_root.Handle) is { } finalPath) _rootPath = finalPath.TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { }
    }

    internal FileAbsence Check(ItemState item)
    {
        if (_root is null || _rootPath is null) return FileAbsence.Unknown;
        try
        {
            var (volume, id) = LocalItems.ParseObjectId(item.ObjectId);
            if (volume != _root.Identity.VolumeSerialNumber || id == 0) return FileAbsence.Unknown;
            var descriptor = new FileIdDescriptor { Size = 24, Type = 0, FileId = id };
            using var file = OpenFileById(_root.Handle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
            if (!file.IsInvalid)
            {
                var path = FinalPath(file);
                if (path is null) return FileAbsence.Unknown;
                if (path.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, _rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return FileAbsence.Present;
                // OpenFileById returns one name, not every hard-link alias. An outside name
                // proves departure only for a single-name object; another name may remain in-root.
                return PilotDirectoryLease.GetFileInformationByHandle(file, out var information) &&
                    information.NumberOfLinks == 1 ? FileAbsence.Gone : FileAbsence.Unknown;
            }
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return FileAbsence.Gone;
            // NTFS uses INVALID_PARAMETER for retired references. Prove API support first.
            if (error == 87)
            {
                descriptor.FileId = _root.Identity.FileId;
                using var probe = OpenFileById(_root.Handle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
                if (!probe.IsInvalid) return FileAbsence.Gone;
            }
            return FileAbsence.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return FileAbsence.Unknown;
        }
    }

    private string? FinalPath(SafeFileHandle handle)
    {
        var length = GetFinalPathNameByHandle(handle, _pathBuffer, (uint)_pathBuffer.Length, 0);
        return length == 0 || length >= _pathBuffer.Length ? null : new string(_pathBuffer, 0, (int)length);
    }

    public void Dispose() => _root?.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public ulong FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id,
        uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint length, uint flags);
}
