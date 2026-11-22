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
    private const int ErrorInvalidParameter = 87;
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

    /// <summary>Lists native entries only after authenticating the root on the held enumeration handle.</summary>
    public static async IAsyncEnumerable<Listed> ReadAsync(string root, DirectoryIdentity expectedRoot,
        PathScope paths, string under, ListOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var held = new Stack<SafeFileHandle>();
        try
        {
            var folder = string.Empty;
            long completed = 0;
            var startingId = unchecked((long)expectedRoot.FileId);
            try
            {
                var rootHandle = DriveIdentity.Open(root, list: true, pin: true);
                held.Push(rootHandle);
                if (DriveIdentity.FromHandle(rootHandle) != expectedRoot) throw new ChangedUnderneathException(root);
                if (under.Length != 0)
                {
                    if (Path.IsPathRooted(under)) throw new ArgumentException(Text.Get(TextKey.RelativePathRequired), nameof(under));
                    foreach (var name in PathNames.NormalizeRelative(under).Split('/'))
                    {
                        ct.ThrowIfCancellationRequested();
                        folder = PathNames.Child(folder, name);
                        if (!options.IncludeOutOfScope && paths.Excludes(folder, null, true)) break;
                        var info = Named(held.Peek(), name);
                        if (info is null || !info.Attributes.HasFlag(FileAttributes.Directory) || Placeholders.Skip(info.Attributes, info.ReparseTag) is not null)
                            throw new ChangedUnderneathException(folder);
                        held.Push(OpenListed(held.Peek(), info.Name, info.FileId, folder));
                        startingId = info.FileId;
                    }
                }
            }
            catch (Exception e) when (LocalErrors.IsFileFailure(e) || e is ChangedUnderneathException)
            {
                throw new InventoryIncompleteException(e.Message, e);
            }
            if (folder.Length != 0 && !options.IncludeOutOfScope && paths.Excludes(folder, null, true)) yield break;

            // Finish one native buffer before descending. Keep ancestors pinned, not buffers for every depth.
            var pending = new Stack<(Entry? Child, long Id, string? Close)>();
            pending.Push((null, 0, null));
            while (pending.TryPop(out var visit))
            {
                ct.ThrowIfCancellationRequested();
                if (visit.Close is { } done)
                {
                    held.Pop().Dispose();
                    if (done.Length != 0) options.DirectoryAcquired?.Invoke(done, LocalIdentity.FileIdText(unchecked((ulong)visit.Id)));
                    options.Progress?.Invoke(done, ++completed);
                    continue;
                }
                var observed = visit.Child;
                var path = observed?.Path ?? folder;
                Exception? failure = null;
                if (observed is not null)
                {
                    try { held.Push(OpenListed(held.Peek(), Path.GetFileName(path), visit.Id, path)); }
                    catch (Exception e) when (LocalErrors.IsFileFailure(e) || e is ChangedUnderneathException) { failure = e; }
                }
                if (failure is null)
                {
                    var start = pending.Count;
                    pending.Push((null, observed is null ? startingId : visit.Id, path));
                    using var entries = Enumerate(held.Peek()).GetEnumerator();
                    while (Advance(entries, out failure))
                    {
                        ct.ThrowIfCancellationRequested();
                        var info = entries.Current;
                        var childPath = PathNames.Child(path, info.Name);
                        var child = Classify(childPath, info);
                        if ((!child.IsFolder && StageNames.IsStage(info.Name)) ||
                            (!options.IncludeOutOfScope && paths.Excludes(childPath, null, child.IsFolder))) continue;
                        yield return new(childPath, child);
                        if (options.Recursive && child.IsFolder && child.Skip is null) pending.Push((child, info.FileId, null));
                    }
                    if (failure is null) continue;
                    while (pending.Count > start) pending.Pop();
                    held.Pop().Dispose();
                }
                if (observed is null) throw new InventoryIncompleteException(failure.Message, failure);
                yield return new(path, observed with { Skip = TextKey.SkipUnreadable.ToString() });
            }
        }
        finally { while (held.TryPop(out var handle)) handle.Dispose(); }
    }

    // One direct child of the still-held directory. Opening a substitute by name never authenticates it.
    private static SafeFileHandle OpenListed(SafeFileHandle parent, string name, long listedId, string path)
    {
        var unicode = new UnicodeString { Buffer = name, Length = checked((ushort)(name.Length * sizeof(char))), MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))) };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, buffer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = buffer, Flags = 0x40 }; // OBJ_CASE_INSENSITIVE
            var status = NtOpenFile(out var child, 0x100081, ref attributes, out _, FileShare.ReadWrite, 0x00200021); // SYNCHRONIZE | LIST_DIRECTORY | READ_ATTRIBUTES; OPEN_REPARSE_POINT | SYNCHRONOUS_IO_NONALERT | DIRECTORY_FILE
            GC.KeepAlive(parent);
            try
            {
                if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status));
                var basic = DriveIdentity.Basic(child);
                if (unchecked((long)(((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow)) != listedId ||
                    !basic.FileAttributes.HasFlag(FileAttributes.Directory) ||
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
    /// <summary>Looks up a single long name without reading the child or scanning its siblings.</summary>
    public static Entry? One(string root,string path)
    {
        var full=Full(root,path);
        try
        {
            using var handle = DriveIdentity.Open(Path.GetDirectoryName(full)!, list: true);
            var row = Named(handle, Path.GetFileName(full));
            return row is null ? null : Classify(PathNames.Child(PathNames.Parent(path), row.Name), row);
        }
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
    private static DirectoryEntryInfo? Named(SafeFileHandle handle, string name)
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
        if (nameLength <= 0 || nameLength > MaxNameCharacters * sizeof(char)) throw new InvalidDataException();
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
