using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Pinned-handle Win32 I/O: opens that never follow the leaf, identity reads, verification, rename and discard by handle.</summary>
internal static class LocalNative
{
    // Native handles stay open across verification and mutation. OPEN_REPARSE_POINT never follows the leaf.
    internal const uint ReadData=0x80000000,WriteData=0x40000000,DeleteAccess=0x10000,ReadAttributes=0x80,WriteAttributes=0x100;
    internal static bool IsLink(FileAttributes a)=>(a&FileAttributes.ReparsePoint)!=0;
    private const uint NoRecallAndOpenReparsePoint=0x00300000, BackupSemantics=0x02000000;
    private const uint OpenExisting = 3; // OPEN_EXISTING (fileapi.h)
    internal static SafeFileHandle Open(string path,uint access,FileShare share,bool create=false)
    {
        var handle=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),access,(uint)share,IntPtr.Zero,create?1u:3u,NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(!handle.IsInvalid) return handle;
        var code=Marshal.GetLastWin32Error(); handle.Dispose(); throw Native(code);
    }
    internal static IOException Native(int code)=>new(new Win32Exception(code).Message,unchecked((int)(0x80070000u|(uint)code)));
    internal static HeldInfo Info(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle,out var i)) throw Native(Marshal.GetLastWin32Error());
        var identity=new DirectoryIdentity(i.Volume,((ulong)i.IndexHigh<<32)|i.IndexLow,null);
        if(GetFileInformationByHandleEx(handle,18,out WideId wide,24)) identity=new(wide.Volume,wide.Low,wide.High);
        else if(Marshal.GetLastWin32Error() is not (1 or 50 or 87 or 120 or 124)) throw Native(Marshal.GetLastWin32Error());
        return new(identity,(FileAttributes)i.Attributes,
            checked((long)(((ulong)i.SizeHigh<<32)|i.SizeLow)),DateTimeOffset.FromFileTime(((long)i.WriteHigh<<32)|i.WriteLow).ToUniversalTime());
    }
    internal static HeldInfo Verify(SafeFileHandle handle,Entry entry,string domain,bool allowPlaceholder=false)
    {
        var info=Info(handle);
        var classification=Classify(handle,info.Attributes);
        if(!Matches(info,entry,domain,classification,allowPlaceholder)) throw new ChangedUnderneathException(entry.Path);
        return info;
    }
    internal static bool Matches(HeldInfo info,Entry entry,string domain,TextKey? classification,bool allowPlaceholder) =>
        info.Id==entry.Id && info.Domain==domain && info.Folder==entry.IsFolder && (info.Folder || (info.Size==entry.Size && info.Modified==entry.Modified)) &&
        classification?.ToString()==entry.Skip && (classification is null || (allowPlaceholder && classification==TextKey.SkipOnlineOnly));
    // The metadata handle selected the object, not just a reusable pathname. Reclassify on the data handle
    // before reading even one byte. A receipt takes its metadata and digest under this one writer-denying open.
    internal static FileStream ReadContent(SafeFileHandle metadata,string path,FileShare share,out HeldInfo observed)
    {
        var before=Info(metadata);
        if(before.Folder || Classify(metadata,before.Attributes) is not null) throw new ChangedUnderneathException(path);
        var bytes=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),ReadData,(uint)share,IntPtr.Zero,OpenExisting,NoRecallAndOpenReparsePoint,IntPtr.Zero);
        try
        {
            if(bytes.IsInvalid) throw Native(Marshal.GetLastWin32Error());
            observed=Info(bytes);
            if(observed.Identity!=before.Identity || observed.Folder || Classify(bytes,observed.Attributes) is not null)
                throw new ChangedUnderneathException(path);
            return new FileStream(bytes,FileAccess.Read);
        }
        catch { bytes.Dispose(); throw; }
    }
    internal static TextKey? Classify(SafeFileHandle handle,FileAttributes attributes)
    {
        // Plain files have no reparse tag or recall attributes to classify.
        if((attributes & ~(FileAttributes.Normal|FileAttributes.Archive|FileAttributes.Directory))==0) return null;
        if(!GetFileInformationByHandleEx(handle,9,out AttributeTag tag,8)) throw Native(Marshal.GetLastWin32Error());
        return Placeholders.Skip(attributes,tag.Tag);
    }
    internal static void Rename(SafeFileHandle source,SafeFileHandle parent,string name)
    {
        var text=System.Text.Encoding.Unicode.GetBytes(name); int length=IntPtr.Size*2,offset=length+sizeof(int);
        var buffer=Marshal.AllocHGlobal(offset+text.Length+IntPtr.Size);
        bool added=false;
        try
        {
            Marshal.WriteIntPtr(buffer,IntPtr.Zero); parent.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(buffer,IntPtr.Size,parent.DangerousGetHandle()); Marshal.WriteInt32(buffer,length,text.Length);
            Marshal.Copy(text,0,buffer+offset,text.Length);
            var status=NtSetInformationFile(source,out _,buffer,(uint)(offset+text.Length+IntPtr.Size),10);
            if(status<0)
            { var code=(int)RtlNtStatusToDosError(status); if(code is 80 or 183) throw new PathOccupiedException(name,Side.Local); throw Native(code); }
        }
        finally { if(added) parent.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
    }
    internal static void Discard(SafeFileHandle handle)
    {
        byte delete=1; if(!SetFileInformationByHandle(handle,4,ref delete,1)) throw Native(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    { public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes,Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct WideId { public ulong Volume,Low,High; }
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out WideId id,uint length);
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status,Information; }
    [DllImport("kernel32",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] internal static extern bool CreateDirectoryW(string path,IntPtr security);
    [DllImport("kernel32",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfoNative info);
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out AttributeTag tag,uint length);
    [DllImport("kernel32",SetLastError=true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref byte value,uint length);
    [DllImport("ntdll")] private static extern int NtSetInformationFile(SafeFileHandle handle,out IoStatus status,IntPtr info,uint size,int kind);
    [DllImport("ntdll")] private static extern uint RtlNtStatusToDosError(int status);
}
