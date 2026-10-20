using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Explicit single-file Cloud Files hydration. Never called by inventory or hashing.</summary>
internal static class OnlineOnlyHydration
{
    internal const long MaximumBytes = 256L * 1024 * 1024;

    internal static void Validate(ItemState item)
    {
        if (item.Kind != ItemKind.File || !item.Flags.HasFlag(ItemFlags.Placeholder) ||
            (item.Flags & (ItemFlags.Link | ItemFlags.Locked)) != 0)
            throw new SyncRunRefusedException("Choose an online-only regular file, not a folder, link or locked item.");
        if (item.Size is < 0 or > MaximumBytes)
            throw new SyncRunRefusedException("This action downloads at most 256 MiB. Make this file available offline in its provider's app, then Retry; or Leave online-only.");
        if (!PilotPath.IsWindowsTransferPath(item.Path))
            throw new SyncRunRefusedException("The file path is not a safe relative Windows path.");
    }

    internal static Task HydrateAsync(RootDef root, ItemState item, IStateStore store, CancellationToken ct) => Task.Run(() =>
    {
        Validate(item);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Cloud Files is required.");
        var rootId = LocalItems.ParseObjectId(root.RootObjectId);
        using var rootLease = PilotDirectoryLease.OpenDirectoryReadGuard(root.RootPath, new(rootId.Volume, rootId.FileId));
        using var ancestors = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, store, [item.Path]);
        if (ancestors.Safe.Count != 1 || ancestors.Incomplete.Count != 0)
            throw new IOException("The file's parent changed or is redirected. Refresh the inventory first.");
        var path = LocalItems.FullPath(root.RootPath, item.Path);
        // Attribute-only + OPEN_REPARSE_POINT: opening this handle itself never hydrates.
        using var handle = CreateFile(PilotPath.ToExtendedWindowsPath(path), 0x40080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireIdentity(handle, item);
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file moved while its availability was being checked. Refresh the notice first.");
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!PilotPath.IsCloudFilesReparseTag(tag.ReparseTag))
            throw new IOException("This is no longer a Cloud Files placeholder. Nothing was downloaded.");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.AvailableFreeSpace < item.Size + 256L * 1024 * 1024)
            throw new IOException("Free local storage first; hydration retains a 256 MiB safety reserve.");
        ct.ThrowIfCancellationRequested();
        // No recursive flag; exact bounded length, never CF_EOF. This is the only hydration call.
        Check(CfHydratePlaceholder(handle, 0, item.Size, 0, IntPtr.Zero));
        RequireIdentity(handle, item);
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file moved while its availability was being checked. Refresh the notice first.");
        // Pin only after a successful download; failed hydration does not change the provider's pin policy.
        Check(CfSetPinState(handle, 1, 0, IntPtr.Zero));
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var hydrated) ||
            PilotPath.IsOnlineOnlyAttributes(hydrated.FileAttributes))
            throw new IOException("Windows has not confirmed the file is available offline yet.");
        rootLease.VerifyBinding();
    }, ct);

    private static void RequireIdentity(SafeFileHandle handle, ItemState item)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var expected = LocalItems.ParseObjectId(item.ObjectId);
        var size = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        var mtime = DateTimeOffset.FromFileTime(((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime);
        if (info.VolumeSerialNumber != expected.Volume || (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != expected.FileId ||
            info.NumberOfLinks != 1 || (info.FileAttributes & FileAttributes.Directory) != 0 ||
            size != (ulong)item.Size || mtime != item.Mtime)
            throw new IOException("The file identity, size, write time or hard-link count changed. Refresh the notice first.");
    }

    private static void Check(int result)
    {
        if (result < 0) throw new IOException("Windows could not make this file available offline.", Marshal.GetExceptionForHR(result));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [DllImport("cldapi.dll")]
    private static extern int CfHydratePlaceholder(SafeFileHandle handle, long offset, long length, uint flags, IntPtr overlapped);
    [DllImport("cldapi.dll")]
    private static extern int CfSetPinState(SafeFileHandle handle, int state, uint flags, IntPtr overlapped);
}
