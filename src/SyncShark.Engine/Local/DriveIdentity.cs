using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Massdex.SyncShark.Engine.Local;
/// <summary>Reads a supported local root's drive type and native directory identity.</summary>
public static class DriveIdentity
{
    public static (DirectoryIdentity Identity, DriveType Type) Read(string root)
    {
        var full = Path.GetFullPath(root);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal)))
            throw new ItemFailedException(root, TextKey.RootLocalNetworkUnsupported, null);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        var type = drive.DriveType;
        if (type == DriveType.Network) throw new ItemFailedException(root, TextKey.RootLocalNetworkUnsupported, null);
        if (!string.Equals(drive.DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase)) throw new ItemFailedException(root,TextKey.RootLocalFormatUnsupported,null);
        using var handle=Open(root); return (FromHandle(handle),type);
    }
    internal static SafeFileHandle Open(string path,bool list=false,bool pin=false)
    {
        Placeholders.Expose();
        var h=CreateFileW(Path.GetFullPath(path),list ? 0x81u : 0x80u, FileShare.ReadWrite | (pin ? FileShare.None : FileShare.Delete), IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if (!h.IsInvalid) return h;
        var e=Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(e);
    }
    internal static DirectoryIdentity FromHandle(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandleEx(handle,9,out AttributeTag tag,8)) throw new Win32Exception();
        if(!tag.Attributes.HasFlag(FileAttributes.Directory) || (tag.Attributes.HasFlag(FileAttributes.ReparsePoint) && !Placeholders.IsCloudFilesReparseTag(tag.Tag))) throw new IOException(Text.Get(TextKey.RootLocalIdentityUnavailable));
        if(GetFileInformationByHandleEx(handle,18,out FileIdInfoBuffer wide,24)) return new(wide.VolumeSerialNumber,wide.FileIdLow,wide.FileIdHigh);
        var error=Marshal.GetLastWin32Error(); if(error is not (1 or 50 or 87 or 120 or 124)) throw new Win32Exception(error);
        var basic=Basic(handle); return new(basic.VolumeSerialNumber,((ulong)basic.FileIndexHigh<<32)|basic.FileIndexLow,null);
    }
    internal static bool SkipsHeld(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, (uint)Marshal.SizeOf<AttributeTag>())) throw new Win32Exception();
        return Placeholders.Skip(tag.Attributes,tag.Tag) is not null;
    }
    internal static ByHandleFileInformation Basic(SafeFileHandle h) => GetFileInformationByHandle(h,out var b) ? b : throw new Win32Exception();
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle h,out ByHandleFileInformation info);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle h,int kind,out AttributeTag tag,uint length);
    internal struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // FILE_ID_INFO: a 64-bit volume serial and a 128-bit file id (two unsigned 64-bit halves).
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoBuffer
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass,
        out FileIdInfoBuffer lpFileInformation, uint dwBufferSize);
}
