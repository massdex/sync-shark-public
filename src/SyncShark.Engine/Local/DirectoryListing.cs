using System.ComponentModel;
using System.Security.AccessControl;
using static Massdex.SyncShark.Engine.Local.LocalErrors;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;
/// <summary>Enumerates native directory metadata and shares classification with exact-name reads.</summary>
public static class DirectoryListing
{
    private sealed record DirectoryEntryInfo(string Name,long FileId,long Size,long LastWriteFileTime,FileAttributes Attributes,uint ReparseTag);
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryInformation = 37;
    private const int ErrorNoMoreFiles = 18; // winerror.h ERROR_NO_MORE_FILES
    private const string NativeLibrary = "ntdll.dll";
    private const uint FileOpenForBackupIntent = 0x00004000; // wdm.h FILE_OPEN_FOR_BACKUP_INTENT: the native form of FILE_FLAG_BACKUP_SEMANTICS for one leaf open
    // FILE_ID_BOTH_DIR_INFO offsets (Windows SDK default packing; identical on x86/x64/ARM64):
    // NextEntryOffset 0, LastWriteTime 24, EndOfFile 40, FileAttributes 56, FileNameLength 60, EaSize 64,
    // ShortNameLength 68, ShortName[12] 70, FileId 96, FileName 104. For a reparse point the file system reports the
    // reparse tag in EaSize (the same value FindFirstFile returns in dwReserved0), so the listing derives the tag without
    // opening the leaf; a held reparse object's skips-held state is validated separately on its opened handle.
    private const int NextEntryOffsetOffset = 0;
    private const int LastWriteTimeOffset = 24;
    private const int EndOfFileOffset = 40;
    private const int FileAttributesOffset = 56;
    private const int FileNameLengthOffset = 60;
    private const int EaSizeOffset = 64;
    private const int FileIdOffset = 96;
    private const int FileNameOffset = 104;
    // NTFS maximum name length, 255 UTF-16 units.
    internal const int MaxNameCharacters = 255;
    private const int NamedBufferSize = FileNameOffset + MaxNameCharacters * sizeof(char);

    /// <summary>Lists native entries only after authenticating the root on the held enumeration handle.</summary>
    public static async IAsyncEnumerable<Listed> ReadAsync(string root, DirectoryIdentity expectedRoot,
        PathScope paths, string under, ListOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var held = new Stack<SafeFileHandle>();
        try
        {
            var start = (Path: string.Empty, Id: unchecked((long)expectedRoot.FileId));
            held.Push(DriveIdentity.Open(root, list: true, expectedRoot: expectedRoot));
            foreach (var step in StartingFolders(root, under, held.Peek(), paths, options, ct))
            {
                if (step.Handle is null) yield break;
                held.Push(step.Handle);
                start = (step.Path, step.Id);
            }
            var pending = new Stack<(Entry? Child, long Id, string? Close)>();
            long completed = 0;
            pending.Push((null, start.Id, null));
            while (pending.TryPop(out var visit))
            {
                ct.ThrowIfCancellationRequested();
                if (visit.Close is { } done)
                {
                    held.Pop().Dispose();
                    NotifyClosed(done, visit.Id, ++completed, options);
                    continue;
                }
                var path = visit.Child?.Path ?? start.Path;
                Exception? failure = null;
                if (visit.Child is not null && TryOpenChild(held.Peek(), path, visit.Id, out failure) is { } child) held.Push(child);
                if (failure is null)
                {
                    var mark = pending.Count;
                    pending.Push((null, visit.Id, path));
                    using var entries = Enumerate(held.Peek(), path.Length == 0 ? root : null).GetEnumerator();
                    while (Advance(entries, out failure))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (ListEntry(held.Peek(), entries.Current, path, paths, options, pending) is { } row) yield return row;
                    }
                    if (failure is null) continue;
                    while (pending.Count > mark) pending.Pop();
                    held.Pop().Dispose();
                }
                if (visit.Child is null) throw new InventoryIncompleteException(failure.Message, failure);
                yield return new(path, visit.Child with { Skip = TextKey.SkipUnreadable.ToString() });
            }
        }
        finally
        {
            while (held.TryPop(out var handle)) handle.Dispose();
        }
    }

    private static IEnumerable<(string Path, long Id, SafeFileHandle? Handle)> StartingFolders(string root, string under,
        SafeFileHandle parent, PathScope paths, ListOptions options, CancellationToken ct)
    {
        if (under.Length == 0) yield break;
        if (Path.IsPathRooted(under)) throw new ArgumentException(Text.Get(TextKey.RelativePathRequired), nameof(under));
        var folder = string.Empty;
        string? configuredRoot = root;
        foreach (var name in PathNames.NormalizeRelative(under).Split('/'))
        {
            ct.ThrowIfCancellationRequested();
            folder = PathNames.Child(folder, name);
            (string, long, SafeFileHandle?) step = (folder, 0, null);
            try
            {
                if (options.IncludeOutOfScope || !paths.Excludes(folder, null, true))
                {
                    var info = Named(parent, name, configuredRoot);
                    if (info is null || !info.Attributes.HasFlag(FileAttributes.Directory) || Placeholders.Skip(info.Attributes, info.ReparseTag) is not null)
                        throw new ChangedUnderneathException(folder);
                    parent = OpenListed(parent, info.Name, info.FileId, folder, out _);
                    step = (folder, info.FileId, parent);
                }
            }
            catch (Exception e) when (LocalErrors.IsFileFailure(e) || e is ChangedUnderneathException changed && changed.Path != root)
            {
                throw new InventoryIncompleteException(e.Message, e);
            }
            yield return step;
            configuredRoot = null;
        }
    }

    private static void NotifyClosed(string path, long id, long completed, ListOptions options)
    {
        if (path.Length != 0) options.DirectoryAcquired?.Invoke(path, LocalIdentity.FileIdText(unchecked((ulong)id)));
        options.Progress?.Invoke(path, completed);
    }

    private static SafeFileHandle? TryOpenChild(SafeFileHandle parent, string path, long id, out Exception? failure)
    {
        failure = null;
        try { return OpenListed(parent, Path.GetFileName(path), id, path, out _); }
        catch (Exception e) when (LocalErrors.IsFileFailure(e) || e is ChangedUnderneathException) { failure = e; return null; }
    }

    private static Listed? ListEntry(SafeFileHandle parent, DirectoryEntryInfo info, string path, PathScope paths, ListOptions options,
        Stack<(Entry? Child, long Id, string? Close)> pending)
    {
        var childPath = PathNames.Child(path, info.Name);
        var isFolder = info.Attributes.HasFlag(FileAttributes.Directory);
        if (!isFolder && StageNames.IsStage(info.Name)) return null;
        if (!options.IncludeOutOfScope && paths.Excludes(childPath, null, isFolder)) { options.Pruned?.Invoke(childPath); return null; }
        var child = Observe(parent, childPath, info);
        if (options.Recursive && child.IsFolder && child.Skip is null) pending.Push((child, info.FileId, null));
        return new(childPath, child);
    }

    // NTFS refreshes a hard link's directory row only for the name written through, so a listed regular file's size and
    // time come from the file, opened relative to the held parent without data, reparse follow or recall.
    // A vanished name or another object keeps the listing's values; the file's own unusable time is unreadable.
    private static Entry Observe(SafeFileHandle parent, string path, DirectoryEntryInfo row)
    {
        var folder = row.Attributes.HasFlag(FileAttributes.Directory);
        if (!folder && Placeholders.Skip(row.Attributes, row.ReparseTag) is null)
            try
            {
                using (OpenListed(parent, row.Name, row.FileId, path, out var basic, folder: false))
                    return Classify(path, row with { Size = ((long)basic.FileSizeHigh << 32) | basic.FileSizeLow,
                        LastWriteFileTime = ((long)basic.LastWriteTime.dwHighDateTime << 32) | (uint)basic.LastWriteTime.dwLowDateTime }, false);
            }
            catch (Win32Exception e) when (e.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound) { }
            catch (ChangedUnderneathException) { }
            catch (Exception e) when (LocalErrors.IsFileFailure(e)) { return Classify(path, row, false) with { Skip = nameof(TextKey.SkipUnreadable) }; }
        return Classify(path, row, folder);
    }

    // One direct child of the still-held directory. Opening a substitute by name never authenticates it.
    private static SafeFileHandle OpenListed(SafeFileHandle parent, string name, long listedId, string path,
        out DriveIdentity.ByHandleFileInformation basic, bool folder = true)
    {
        var unicode = new UnicodeString { Buffer = name, Length = checked((ushort)(name.Length * sizeof(char))), MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))) };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, buffer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = buffer, Flags = 0x40 }; // OBJ_CASE_INSENSITIVE
            // FILE_DIRECTORY_FILE restricts the allowed open options; require directory kind on the held handle below instead. A file adds
            // FILE_OPEN_FOR_BACKUP_INTENT, the native form of the FILE_FLAG_BACKUP_SEMANTICS every other leaf open here uses (DriveIdentity.Open, LocalNative.Open).
            var openOptions = 0x00400000u | 0x00200000 | 0x20 | (folder ? 0u : FileOpenForBackupIntent); // FILE_OPEN_NO_RECALL | FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT [| FILE_OPEN_FOR_BACKUP_INTENT]
            var status = NtOpenFile(out var child, (uint)(FileSystemRights.Synchronize | FileSystemRights.ReadAttributes | (folder ? FileSystemRights.ListDirectory : (FileSystemRights)0)), ref attributes, out _, FileShare.ReadWrite | FileShare.Delete, openOptions);
            GC.KeepAlive(parent);
            try
            {
                if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status));
                basic = DriveIdentity.Basic(child);
                if (unchecked((long)(((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow)) != listedId ||
                    basic.FileAttributes.HasFlag(FileAttributes.Directory) != folder ||
                    Placeholders.IsOnlineOnlyAttributes(basic.FileAttributes) ||
                    (basic.FileAttributes.HasFlag(FileAttributes.ReparsePoint) && DriveIdentity.SkipsHeld(child))) throw new ChangedUnderneathException(path);
                return child;
            }
            catch { child.Dispose(); throw; }
        }
        finally { Marshal.DestroyStructure<UnicodeString>(buffer); Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        public int Length;
        public IntPtr Root, Name;
        public uint Flags;
        public IntPtr SecurityDescriptor, SecurityQos;
    }
    [DllImport(NativeLibrary)] private static extern int NtOpenFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatusBlock io, FileShare share, uint options);

    private static bool Advance(IEnumerator<DirectoryEntryInfo> children, out Exception? failure)
    {
        failure = null;
        try { return children.MoveNext(); }
        catch (Exception e) when (LocalErrors.IsFileFailure(e)) { failure = e; return false; }
    }
    private static string Full(string root,string path)
    {
        var full=Path.GetFullPath(PathNames.NormalizeRelative(path),Path.GetFullPath(root));
        if(Path.IsPathRooted(path) || PathNames.RelativeUnderLocalRoot(root,full) is null) throw new ArgumentException(Text.Get(TextKey.UnsafePathSegment),nameof(path));
        return full;
    }
    /// <summary>Looks up a single long name without reading file contents or scanning its siblings.</summary>
    public static Entry? One(string root,string path)
    {
        var full=Full(root,path);
        try
        {
            using var handle = DriveIdentity.Open(Path.GetDirectoryName(full)!, list: true);
            var row = Named(handle, Path.GetFileName(full));
            if(row is null) return null;
            return Observe(handle, PathNames.Child(PathNames.Parent(path), row.Name), row);
        }
        catch(Win32Exception e) when(e.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound) { return null; }
    }
    private static Entry Classify(string path, DirectoryEntryInfo row, bool folder)
    {
        var skip = Placeholders.Skip(row.Attributes, row.ReparseTag);
        DateTimeOffset modified = default;
        if (!folder && skip != TextKey.SkipLink)
        {
            try { modified = DateTime.FromFileTimeUtc(row.LastWriteFileTime); }
            catch (ArgumentOutOfRangeException) { skip = TextKey.SkipUnreadable; }
        }
        return new(path, folder, folder ? 0 : row.Size, modified, LocalIdentity.FileIdText(unchecked((ulong)row.FileId)), "", "", skip?.ToString());
    }
    private static IEnumerable<DirectoryEntryInfo> Enumerate(SafeFileHandle handle, string? configuredRoot)
    {
        // .NET FileSystemEnumerator.StandardBufferSize; EnumerationOptions.BufferSize: "Typical is 4K".
        const int bufferSize = 4096;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(handle, FileIdBothDirectoryInfo, buffer, (uint)bufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles) yield break;
                    var failure = new Win32Exception(error);
                    if (configuredRoot is not null) throw new ChangedUnderneathException(configuredRoot, failure);
                    throw failure;
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
    private static DirectoryEntryInfo? Named(SafeFileHandle handle, string name, string? configuredRoot = null)
    {
        if (name.Length == 0 || name.Length > MaxNameCharacters) return null;
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
                if (configuredRoot is not null) throw new ChangedUnderneathException(configuredRoot, new Win32Exception(error));
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
        if (nameLength <= 0 || nameLength > MaxNameCharacters * sizeof(char) || nameLength % sizeof(char) != 0) throw new IOException();
        var name = Marshal.PtrToStringUni(record + FileNameOffset, nameLength / sizeof(char));
        if (name is null or "." or "..") return null;
        var attributes = (FileAttributes)(uint)Marshal.ReadInt32(record, FileAttributesOffset);
        return new DirectoryEntryInfo(name, Marshal.ReadInt64(record, FileIdOffset),
            Marshal.ReadInt64(record, EndOfFileOffset),
            Marshal.ReadInt64(record, LastWriteTimeOffset), attributes,
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
