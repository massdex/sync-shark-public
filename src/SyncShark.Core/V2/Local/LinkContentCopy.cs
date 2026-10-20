using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

/// <summary>Explicit link actions only. Inventory never resolves or copies link targets.</summary>
internal static class LinkContentCopy
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    private const string Options = " Show target, sync the target separately, or Ignore to leave the link untouched.";
    internal sealed record Target(string Path, bool InsideRoot);

    internal static Target Show(string root, string link)
    {
        var path = Path.GetFullPath(link);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var hops = 0; hops < 64; hops++)
        {
            if (!seen.Add(path)) throw new IOException("Link cycle detected." + Options);
            FileSystemInfo info = (File.GetAttributes(path) & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(false);
            if (target is null)
            {
                if (hops == 0) throw new IOException("This item is no longer a supported link." + Options);
                // Do not silently follow a junction in a target's parent path.
                RequireRegularParents(path);
                return new(path, Under(path, root));
            }
            path = Path.GetFullPath(target.FullName);
        }
        throw new IOException("Link chain is cyclic or exceeds 64 hops." + Options);
    }

    internal static void ValidateTarget(string root, string link, Target target, bool allowOutside)
    {
        if (!target.InsideRoot && !allowOutside)
            throw new IOException("Target is outside the pair root. Choose 'Create regular copy — confirm outside-root access' explicitly to permit reading it." + Options);
        if (Under(link, target.Path)) throw new IOException("Copy would recurse into itself: the target is an ancestor of the link." + Options);
        PilotPath.RequireSeparatedFromProductState(target.Path);
    }

    private static bool Under(string path, string root) => string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequireRegularParents(string path)
    {
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new IOException("A target parent is redirected or online-only; choose its regular physical location instead." + Options);
    }

    internal sealed class Prepared : IDisposable
    {
        private readonly CancellationToken _ct;
        private readonly List<IDisposable> _leases = [];
        private readonly List<(string Relative, FileStream? Content)> _entries = [];
        private readonly Dictionary<string, PilotDirectoryLease> _parents = new(StringComparer.OrdinalIgnoreCase);
        internal long Bytes { get; private set; }
        private PilotDirectoryLease HoldDirectory(string path)
        {
            path = Path.GetFullPath(path);
            if (_parents.TryGetValue(path, out var held)) return held;
            var parent = Directory.GetParent(path);
            if (parent is not null) HoldDirectory(parent.FullName);
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new IOException("A directory is redirected or online-only." + Options);
            var lease = parent is null ? PilotDirectoryLease.OpenVolumeRootForAllocation(path) : PilotDirectoryLease.Open(path);
            // A read guard denies delete-sharing while the source tree is copied.
            if (parent is not null)
            {
                var guard = PilotDirectoryLease.OpenDirectoryReadGuard(path, lease.Identity);
                lease.Dispose();
                lease = guard;
            }
            _leases.Add(lease);
            // Freeze reparse metadata as well as renames while path-based enumeration is in use.
            // A read-only directory handle with no WRITE/DELETE sharing rejects concurrent redirection.
            var guardHandle = Open(path, true);
            _leases.Add(guardHandle);
            if (!PilotDirectoryLease.GetFileInformationByHandle(guardHandle, out var heldInfo) ||
                (heldInfo.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(PilotDirectoryLease.FinalPath(guardHandle).TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("A directory changed during inspection." + Options);
            _parents.Add(path, lease);
            return lease;
        }

        internal Prepared(RootDef root, ItemState item, bool allowOutside, CancellationToken ct = default)
        {
            _ct = ct;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Link copies require Windows NTFS guards.");
                if (!PilotPath.IsWindowsTransferPath(item.Path)) throw new IOException("Unsafe link path." + Options);
                var rootLease = HoldDirectory(root.RootPath);
                var id = LocalItems.ParseObjectId(root.RootObjectId);
                if (rootLease.Identity != new PilotDirectoryIdentity(id.Volume, id.FileId)) throw new IOException("Pair root identity changed.");
                var link = LocalItems.FullPath(root.RootPath, item.Path);
                HoldDirectory(Path.GetDirectoryName(link)!);
                using var linkHandle = Open(link, false);
                if (!PilotDirectoryLease.GetFileInformationByHandle(linkHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var expected = LocalItems.ParseObjectId(item.ObjectId);
                if (info.VolumeSerialNumber != expected.Volume || (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != expected.FileId ||
                    (info.FileAttributes & FileAttributes.ReparsePoint) == 0)
                    throw new IOException("Link identity changed. Refresh the link list first.");
                var target = Show(root.RootPath, link);
                ValidateTarget(root.RootPath, link, target, allowOutside);
                HoldDirectory(Path.GetDirectoryName(target.Path)!);
                Scan(target.Path, "", 0);
                // Target changes during preflight never broaden outside-root permission.
                if (Show(root.RootPath, link) != target) throw new IOException("Link target changed during inspection. Retry.");
            }
            catch { Dispose(); throw; }
        }

        private void Scan(string path, string relative, int depth)
        {
            _ct.ThrowIfCancellationRequested();
            if (_entries.Count >= MaximumEntries || depth > 64) throw new IOException("Copy exceeds 4096 entries or 64 directory levels." + Options);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Target contains another link (possibly a loop). Nested links are never followed." + Options);
            if (PilotPath.IsOnlineOnlyAttributes(attributes)) throw new IOException("Target contains online-only content. Make it available offline first." + Options);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                HoldDirectory(path);
                _entries.Add((relative, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    Scan(child, relative.Length == 0 ? Path.GetFileName(child) : Path.Combine(relative, Path.GetFileName(child)), depth + 1);
                return;
            }
            var handle = Open(path, true);
            try
            {
                if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info) ||
                    (info.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    PilotPath.IsOnlineOnlyAttributes(info.FileAttributes) ||
                    !string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Source changed or is not a regular offline file." + Options);
                var stream = new FileStream(handle, FileAccess.Read);
                _leases.Add(stream);
                Bytes = checked(Bytes + stream.Length);
                if (Bytes > MaximumBytes) throw new IOException("Copy exceeds the 256 MiB bound." + Options);
                _entries.Add((relative, stream));
            }
            catch { handle.Dispose(); throw; }
        }

        internal async Task CopyAsync(string destination, CancellationToken ct)
        {
            var parent = HoldDirectory(Path.GetDirectoryName(destination)!);
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < Bytes + MaximumBytes)
                throw new IOException("Free storage first; a link copy retains a 256 MiB reserve." + Options);
            var destinations = new Dictionary<string, PilotDirectoryLease>(StringComparer.OrdinalIgnoreCase) { [""] = parent };
            foreach (var (relative, content) in _entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = relative.Length == 0 ? destination : Path.Combine(destination, relative);
                var parentKey = relative.Length == 0 ? "" : Path.GetDirectoryName(relative) ?? "";
                var directory = destinations[parentKey];
                directory.VerifyBinding();
                if (content is null)
                {
                    var created = PilotDirectoryLease.CreateChild(directory, Path.GetFileName(path), hidden: false);
                    _leases.Add(created);
                    destinations[relative] = created;
                }
                else
                {
                    // Parent leases prevent replacement; CreateNew never truncates or follows an existing link.
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await content.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
            _leases.Clear();
        }
    }

    private static SafeFileHandle Open(string path, bool content)
    {
        var handle = CreateFile(PilotPath.ToExtendedWindowsPath(path), content ? 0x80000080u : 0x80u,
            content ? 1u : 3u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
