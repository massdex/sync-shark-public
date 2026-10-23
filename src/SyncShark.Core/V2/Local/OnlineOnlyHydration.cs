using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Explicit single-file Cloud Files hydration. Never called by inventory or hashing.</summary>
internal static class OnlineOnlyHydration
{
    internal const long ConfirmationBytes = 256L * 1024 * 1024;

    internal static long HydrationLength(ItemState item) => item.Size == 0 ? -1 : item.Size;

    internal static void Validate(ItemState item)
    {
        if (item.Kind != ItemKind.File || !item.Flags.HasFlag(ItemFlags.Placeholder) ||
            (item.Flags & (ItemFlags.Link | ItemFlags.Locked)) != 0)
            throw new SyncRunRefusedException("Choose an online-only regular file, not a folder, link or locked item.");
        if (item.Size < 0) throw new SyncRunRefusedException("Refresh the unknown file size before downloading.");
        if (!PilotPath.IsWindowsTransferPath(item.Path))
            throw new SyncRunRefusedException("The file path is not a safe relative Windows path.");
    }

    internal static Task HydrateAsync(RootDef root, ItemState item, IStateStore store, CancellationToken ct,
        Action hydrationCompleted) => Task.Run(() =>
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
        using var opened = WindowsCloudFilesProvider.OpenAttributes(path, writeDac: true, exclusive: false);
        var handle = opened.Handle;
        RequireIdentity(handle, item);
        RequirePath(handle, path);
        if (!GetFileInformationByHandleEx(handle, 9, out var tag, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!PilotPath.IsCloudFilesReparseTag(tag.ReparseTag))
            throw new IOException("This is no longer a Cloud Files placeholder. Nothing was downloaded.");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.AvailableFreeSpace - ConfirmationBytes < item.Size)
            throw new IOException("Free local storage first; hydration retains a 256 MiB safety reserve.");
        ct.ThrowIfCancellationRequested();
        // CF_EOF supports empty placeholders too. Our fetch callback calls DropboxRangeSource
        // directly, not SyncRun or its gate (see ProductionEndpoints.CreateCloudFiles).
        Check(WindowsCloudFilesProvider.Native.CfHydratePlaceholder(handle.DangerousGetHandle(), 0, HydrationLength(item), 0, IntPtr.Zero));
        // Persist verification debt before identity/path/pin/attribute verification can fail.
        hydrationCompleted();
        RequireIdentity(handle, item);
        RequirePath(handle, path);
        Check(WindowsCloudFilesProvider.Native.CfSetPinState(handle.DangerousGetHandle(), 1, 0, IntPtr.Zero));
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var hydrated) ||
            PilotPath.IsOnlineOnlyAttributes(hydrated.FileAttributes))
            throw new IOException("Windows has not confirmed the file is available offline yet.");
        rootLease.VerifyBinding();
    }, ct);

    private static void RequirePath(SafeFileHandle handle, string path)
    {
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file moved while its availability was being checked. Retry to re-observe it.");
    }

    private static void RequireIdentity(SafeFileHandle handle, ItemState item)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireIdentity(info, item);
    }

    internal static void RequireIdentity(PilotDirectoryLease.ByHandleFileInformation info, ItemState item)
    {
        var expected = LocalItems.ParseObjectId(item.ObjectId);
        var size = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        var mtime = DateTimeOffset.FromFileTime(((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime);
        if (info.VolumeSerialNumber != expected.Volume || (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != expected.FileId ||
            info.NumberOfLinks != 1 || (info.FileAttributes & FileAttributes.Directory) != 0 ||
            size != (ulong)item.Size || mtime != item.Mtime)
            throw new IOException("The file identity, size, write time or hard-link count changed. Retry to re-observe it.");
    }

    private static void Check(int result)
    {
        if (result < 0) throw new IOException("Windows could not make this file available offline.", Marshal.GetExceptionForHR(result));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
}
