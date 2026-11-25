using System.ComponentModel;
using System.Globalization;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Pinned-handle Win32 I/O: opens that never follow the leaf, identity reads, verification, rename and discard by handle.</summary>
internal static class LocalNative
{
    // Native handles stay open across verification and mutation. OPEN_REPARSE_POINT never follows the leaf.
    internal const uint ReadData=0x80000000, WriteData=0x40000000; // winnt.h GENERIC_READ, GENERIC_WRITE
    internal const uint DeleteAccess=(uint)FileSystemRights.Delete, Traverse=(uint)FileSystemRights.Traverse,
        ReadAttributes=(uint)FileSystemRights.ReadAttributes, WriteAttributes=(uint)FileSystemRights.WriteAttributes;
    internal static bool IsLink(FileAttributes a)=>(a&FileAttributes.ReparsePoint)!=0;
    internal const uint BackupSemantics=0x02000000; // winbase.h FILE_FLAG_BACKUP_SEMANTICS
    private const uint OpenReparsePoint=0x00200000; // winbase.h FILE_FLAG_OPEN_REPARSE_POINT
    internal const uint NoRecallAndOpenReparsePoint=DriveIdentity.OpenNoRecall | OpenReparsePoint;
    internal const int FileAttributeTagInfo=9, FileIdInfo=18; // minwinbase.h FILE_INFO_BY_HANDLE_CLASS
    private const uint HResultFromWin32Base=0x80070000; // winerror.h HRESULT_FROM_WIN32: severity error, FACILITY_WIN32
    internal static SafeFileHandle Open(string path,uint access,FileShare share,bool create=false)
    {
        var handle=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),access,(uint)share,IntPtr.Zero,(uint)(create?FileMode.CreateNew:FileMode.Open),NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(!handle.IsInvalid) return handle;
        var code=Marshal.GetLastWin32Error(); handle.Dispose(); throw Native(code);
    }
    internal static string Domain(DirectoryIdentity id)=>id.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
    internal static IOException Native(int code)=>new(new Win32Exception(code).Message,unchecked((int)(HResultFromWin32Base|(uint)code)));
    internal static HeldInfo Info(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle,out var i)) throw Native(Marshal.GetLastWin32Error());
        var identity=new DirectoryIdentity(i.Volume,((ulong)i.IndexHigh<<32)|i.IndexLow,null);
        if(GetFileInformationByHandleEx(handle,FileIdInfo,out WideId wide,(uint)Marshal.SizeOf<WideId>())) identity=new(wide.Volume,wide.Low,wide.High);
        else
        {
            var error=Marshal.GetLastWin32Error();
            if(!LocalErrors.FileIdInfoUnsupported(error)) throw Native(error);
        }
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
        var bytes=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),ReadData,(uint)share,IntPtr.Zero,(uint)FileMode.Open,NoRecallAndOpenReparsePoint,IntPtr.Zero);
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
        if(!GetFileInformationByHandleEx(handle,FileAttributeTagInfo,out AttributeTag tag,(uint)Marshal.SizeOf<AttributeTag>())) throw Native(Marshal.GetLastWin32Error());
        return Placeholders.Skip(attributes,tag.Tag);
    }
    internal static void Rename(SafeFileHandle source,SafeFileHandle parent,string name)
    {
        // ntifs.h FILE_RENAME_INFORMATION: pointer-aligned ReplaceIfExists, RootDirectory, then FileNameLength.
        var text=System.Text.Encoding.Unicode.GetBytes(name); int length=IntPtr.Size*2,offset=length+sizeof(int);
        var buffer=Marshal.AllocHGlobal(offset+text.Length+IntPtr.Size);
        bool added=false;
        try
        {
            Marshal.WriteIntPtr(buffer,IntPtr.Zero); parent.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(buffer,IntPtr.Size,parent.DangerousGetHandle()); Marshal.WriteInt32(buffer,length,text.Length);
            Marshal.Copy(text,0,buffer+offset,text.Length);
            var status=NtSetInformationFile(source,out _,buffer,(uint)(offset+text.Length+IntPtr.Size),10); // ntifs.h FileRenameInformation
            if(status<0)
            { var code=(int)RtlNtStatusToDosError(status); if(code is 80 or LocalErrors.ErrorAlreadyExists) throw new PathOccupiedException(name,Side.Local); throw Native(code); } // winerror.h ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS
        }
        finally { if(added) parent.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
    }
    internal static void Discard(SafeFileHandle handle)
    {
        byte delete=1; if(!SetFileInformationByHandle(handle,4,ref delete,sizeof(byte))) throw Native(Marshal.GetLastWin32Error()); // minwinbase.h FileDispositionInfo
    }
    // One app-owned alternate data stream holds one KEEP token. A Guid in N format is 32 characters, so the slot is fixed.
    internal const string KeepStreamName="SyncShark.keep";
    private const int KeepTokenBytes=32, ErrorNotSupportedStream=50; // winerror.h ERROR_NOT_SUPPORTED: a stream on a stream-less volume
    /// <summary>The token this object carries in Sync Shark's own stream, or null when the stream (or object) is absent or the volume has no streams. Other errors are failures.</summary>
    internal static string? ReadKeepStream(string full)
    {
        var handle=CreateFileW(DriveIdentity.ToExtendedWindowsPath(full+":"+KeepStreamName),ReadData,FileShare.ReadWrite,IntPtr.Zero,(uint)FileMode.Open,NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(handle.IsInvalid)
        {
            var code=Marshal.GetLastWin32Error(); handle.Dispose();
            if(code is LocalErrors.ErrorFileNotFound or LocalErrors.ErrorPathNotFound or ErrorNotSupportedStream or LocalErrors.ErrorInvalidFunction) return null;
            throw Native(code);
        }
        using(handle)
        {
            var buffer=new byte[KeepTokenBytes];
            if(!ReadFile(handle,buffer,(uint)buffer.Length,out var read,IntPtr.Zero)) throw Native(Marshal.GetLastWin32Error());
            return read==0?null:System.Text.Encoding.UTF8.GetString(buffer,0,(int)read);
        }
    }
    /// <summary>Write the token to this object's own stream, holding the verified base open so a vanished name is never recreated. False only when the volume has no alternate streams.</summary>
    internal static bool WriteKeepStream(string full,string token)
    {
        using var held=Open(full,ReadAttributes,FileShare.ReadWrite); // OPEN_EXISTING: the base must already exist and is pinned while we write its stream.
        var stream=CreateFileW(DriveIdentity.ToExtendedWindowsPath(full+":"+KeepStreamName),WriteData,FileShare.ReadWrite,IntPtr.Zero,(uint)FileMode.Create,NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(stream.IsInvalid)
        {
            var code=Marshal.GetLastWin32Error(); stream.Dispose();
            if(code is ErrorNotSupportedStream or LocalErrors.ErrorInvalidFunction) return false;
            throw Native(code);
        }
        using(stream)
        {
            var bytes=System.Text.Encoding.UTF8.GetBytes(token);
            if(!WriteFile(stream,bytes,(uint)bytes.Length,out _,IntPtr.Zero)) throw Native(Marshal.GetLastWin32Error());
            return true;
        }
    }
    /// <summary>Remove this object's own stream; a missing object or stream is nothing to do and other errors are failures.</summary>
    internal static void ClearKeepStream(string full)
    {
        var handle=CreateFileW(DriveIdentity.ToExtendedWindowsPath(full+":"+KeepStreamName),DeleteAccess|ReadAttributes,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,(uint)FileMode.Open,NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(handle.IsInvalid)
        {
            var code=Marshal.GetLastWin32Error(); handle.Dispose();
            if(code is LocalErrors.ErrorFileNotFound or LocalErrors.ErrorPathNotFound or ErrorNotSupportedStream or LocalErrors.ErrorInvalidFunction) return;
            throw Native(code);
        }
        using(handle) Discard(handle);
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
    [DllImport("kernel32",SetLastError=true)] private static extern bool ReadFile(SafeFileHandle handle,byte[] buffer,uint toRead,out uint read,IntPtr overlapped);
    [DllImport("kernel32",SetLastError=true)] private static extern bool WriteFile(SafeFileHandle handle,byte[] buffer,uint toWrite,out uint written,IntPtr overlapped);
    [DllImport("ntdll")] private static extern int NtSetInformationFile(SafeFileHandle handle,out IoStatus status,IntPtr info,uint size,int kind);
    [DllImport("ntdll")] private static extern uint RtlNtStatusToDosError(int status);
}
