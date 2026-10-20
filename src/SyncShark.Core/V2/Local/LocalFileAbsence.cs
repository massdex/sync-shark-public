using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

internal enum FileAbsence { Unknown, Present, Gone }

/// <summary>Absence during a partial scan must be proved by identity, never by the old pathname.</summary>
internal static class LocalFileAbsence
{
    internal static FileAbsence Check(string rootPath, PilotDirectoryIdentity rootIdentity, ItemState item)
    {
        if (!OperatingSystem.IsWindows()) return FileAbsence.Unknown;
        try
        {
            var (volume, id) = LocalItems.ParseObjectId(item.ObjectId);
            if (volume != rootIdentity.VolumeSerialNumber || id == 0) return FileAbsence.Unknown;
            using var root = PilotDirectoryLease.Open(rootPath, rootIdentity);
            var descriptor = new FileIdDescriptor { Size = 24, Type = 0, FileId = id };
            using var file = OpenFileById(root.Handle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
            if (!file.IsInvalid) return FileAbsence.Present;
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return FileAbsence.Gone;
            // NTFS reports a retired file reference as INVALID_PARAMETER, not FILE_NOT_FOUND.
            // Prove that this exact descriptor format/access/flags works on this verified volume first;
            // otherwise error 87 could mean an unsupported API and must remain unknown.
            if (error == 87)
            {
                descriptor.FileId = rootIdentity.FileId;
                using var probe = OpenFileById(root.Handle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
                if (!probe.IsInvalid) return FileAbsence.Gone;
            }
            // Access denied, sharing violations and unavailable volumes are NOT absence.
            return FileAbsence.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return FileAbsence.Unknown;
        }
    }

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
}
