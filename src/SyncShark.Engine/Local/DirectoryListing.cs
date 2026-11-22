using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
namespace Massdex.SyncShark.Engine.Local;
/// <summary>Enumerates native directory metadata and shares classification with exact-name reads.</summary>
public static class DirectoryListing
{
    private sealed record DirectoryEntryInfo(string Name,long FileId,long Size,DateTime LastWriteTimeUtc,FileAttributes Attributes,uint ReparseTag);
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
    private const int MaxNameCharacters = 255;
    private const int NamedBufferSize = FileNameOffset + MaxNameCharacters * sizeof(char);

    /// <summary>Reads the requested scope from native enumeration handles.</summary>
    public static async IAsyncEnumerable<Listed> ReadAsync(string root, DirectoryIdentity expectedRoot, PathScope paths,string under,ListOptions options,[EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); await Task.CompletedTask;
        using var held=DriveIdentity.Open(root,list:true,pin:true);
        var actual=DriveIdentity.FromHandle(held);
        if(actual!=expectedRoot) throw new ChangedUnderneathException(root);
        var pending=new Stack<(string Path,string? Id)>();
        if(under.Length!=0 && !options.IncludeOutOfScope && paths.Excludes(under,null,true)) yield break;
        pending.Push((under,null)); long count=0;
        while(pending.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested(); string acquired;
            using(var child=next.Path.Length==0 ? null : DriveIdentity.Open(Full(root,next.Path),list:true))
            {
                var handle=child??held; var identity=child is null ? actual : DriveIdentity.FromHandle(child);
                acquired=LocalIdentity.FileIdText(identity.FileId);
                if(options.RequireListedIdentity && next.Id is not null && next.Id!=acquired) throw new ChangedUnderneathException(next.Path);
                foreach(var item in Enumerate(handle))
                {
                    ct.ThrowIfCancellationRequested(); var path=PathNames.Child(next.Path,item.Name); var entry=Classify(path,item);
                    if((!entry.IsFolder && StageNames.IsStage(item.Name)) || (!options.IncludeOutOfScope && paths.Excludes(path,null,entry.IsFolder))) continue;
                    yield return new(path,entry);
                    if(options.Recursive && entry.IsFolder && entry.Skip is null) pending.Push((path,entry.Id));
                }
            }
            if(next.Path.Length!=0) options.DirectoryAcquired?.Invoke(next.Path,acquired);
            options.Progress?.Invoke(next.Path,++count);
        }
    }
    private static string Full(string root,string path)
    {
        var full=Path.GetFullPath(PathNames.NormalizeRelative(path),Path.GetFullPath(root));
        if(Path.IsPathRooted(path) || PathNames.RelativeUnderLocalRoot(root,full) is null) throw new ArgumentException(Text.Get(TextKey.UnsafePathSegment),nameof(path));
        return full;
    }
    /// <summary>Looks up a single long name without reading the child or scanning its siblings.</summary>
    public static Entry? One(string root,string path)
    {
        var full=Full(root,path);
        try { var row=Named(Path.GetDirectoryName(full)!,Path.GetFileName(full)); return row is null ? null : Classify(PathNames.Child(PathNames.Parent(path),row.Name),row); }
        catch(Win32Exception e) when(e.NativeErrorCode is 2 or 3) { return null; }
    }
    private static Entry Classify(string path,DirectoryEntryInfo row)
    {
        var folder=row.Attributes.HasFlag(FileAttributes.Directory);
        return new(path,folder,folder ? 0 : row.Size,folder ? default : new DateTimeOffset(row.LastWriteTimeUtc),LocalIdentity.FileIdText(unchecked((ulong)row.FileId)),"","",Placeholders.Skip(row.Attributes,row.ReparseTag)?.ToString());
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
                    throw new Win32Exception(error);
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
    private static DirectoryEntryInfo? Named(string directoryPath, string name)
    {
        if (name.Length == 0 || name.Length > MaxNameCharacters) return null;
        using var handle = DriveIdentity.Open(directoryPath,list:true);
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
                    (DriveIdentity.Basic(handle).FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
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

}
