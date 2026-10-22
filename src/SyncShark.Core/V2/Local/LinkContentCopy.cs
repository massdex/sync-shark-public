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
    internal static Action<string>? EntryCopiedForTest { get; set; }
    internal sealed record Target(string Path, bool InsideRoot);

    internal static Target Show(string root, string link)
    {
        var path = Path.GetFullPath(link);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var hops = 0; hops < 64; hops++)
        {
            using var handle = Open(path, false);
            var canonical = PilotDirectoryLease.FinalPath(handle);
            if (!string.Equals(canonical.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Target uses an alias (short name, substituted drive, or redirected parent). Recreate the link at the physical path " + canonical + ", or sync that path separately." + Options);
            if (!seen.Add(canonical)) throw new IOException("Link cycle detected." + Options);
            var (attributes, tag) = PilotPath.AttributeTag(handle);
            if (!PilotPath.IsLink(attributes, tag))
            {
                if (hops == 0) throw new IOException("This item is no longer a supported link." + Options);
                if (PilotPath.IsUnsafeHandle(handle, attributes)) throw new IOException("Target is online-only or has an unsupported reparse tag." + Options);
                if (Directory.GetParent(path) is null) throw new IOException("Target is a whole volume. Sync it separately." + Options);
                return new(canonical, Under(canonical, root));
            }
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            path = Path.GetFullPath(info.ResolveLinkTarget(false)?.FullName ?? throw new IOException("Unsupported link target." + Options));
        }
        throw new IOException("Link chain is cyclic or exceeds 64 hops." + Options);
    }

    internal static void ValidateTarget(string root, string link, Target target, bool allowOutside, string? confirmedTarget = null)
    {
        if (allowOutside && (confirmedTarget is null || !Path.IsPathFullyQualified(confirmedTarget) ||
            !string.Equals(Path.GetFullPath(confirmedTarget), target.Path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The confirmed target does not match the current target: " + target.Path + ". Show target again and confirm that exact path with --name." + Options);
        if (!target.InsideRoot && !allowOutside)
            throw new IOException("Target is outside the pair root: " + target.Path + ". Choose 'Create regular copy — confirm target access' with this exact target path to permit reading it." + Options);
        if (Under(link, target.Path)) throw new IOException("Copy would recurse into itself: the target is an ancestor of the link." + Options);
        PilotPath.RequireSeparatedFromProductState(target.Path);
    }

    internal static bool Under(string path, string root) => string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The regular-copy name beside the link; a file keeps its extension last so it opens normally.</summary>
    internal static string CopyName(string leaf, ItemKind kind, int suffix)
    {
        var marker = " (copy" + (suffix == 1 ? "" : " " + suffix) + ")";
        if (kind != ItemKind.File) return leaf + marker;
        var stem = Path.GetFileNameWithoutExtension(leaf);
        return stem.Length == 0 ? leaf + marker : stem + marker + Path.GetExtension(leaf);
    }

    internal sealed class Prepared : IDisposable
    {
        private readonly CancellationToken _ct;
        private readonly List<IDisposable> _leases = [];
        private readonly List<(string Relative, FileStream? Content)> _entries = [];
        private readonly Dictionary<string, PilotDirectoryLease> _parents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool>? _requiresConfirmation;
        private readonly bool _confirmed;
        internal long Bytes { get; private set; }

        private PilotDirectoryLease HoldDirectory(string path, bool source = false)
        {
            path = Path.GetFullPath(path);
            if (!_parents.TryGetValue(path, out var lease))
            {
                // No ancestor walk: a held handle's FinalPath proves the binding. In particular,
                // a parent containing product data is not itself a source or destination.
                lease = PilotDirectoryLease.OpenProductDirectoryReadGuard(path);
                _leases.Add(lease);
                _parents.Add(path, lease);
            }
            if (source)
            {
                PilotPath.RequireSeparatedFromProductState(path);
                var guard = Open(path, true);
                _leases.Add(guard);
                if (!PilotDirectoryLease.GetFileInformationByHandle(guard, out var info) ||
                    PilotPath.IsUnsafeHandle(guard, info.FileAttributes) ||
                    !string.Equals(PilotDirectoryLease.FinalPath(guard), path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Source directory changed or is online-only." + Options);
            }
            return lease;
        }

        internal Prepared(RootDef root, ItemState item, bool allowOutside, CancellationToken ct = default,
            string? confirmedTarget = null, Func<string, bool>? requiresConfirmation = null)
        {
            _ct = ct;
            _requiresConfirmation = requiresConfirmation;
            _confirmed = allowOutside;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Link copies require Windows NTFS guards.");
                if (!PilotPath.IsWindowsTransferPath(item.Path)) throw new IOException("Unsafe link path." + Options);
                PilotPath.RequireSeparatedFromProductState(root.RootPath);
                var rootLease = HoldDirectory(root.RootPath);
                var id = LocalItems.ParseObjectId(root.RootObjectId);
                if (rootLease.Identity != new PilotDirectoryIdentity(id.Volume, id.FileId)) throw new IOException("Pair root identity changed.");
                var link = LocalItems.FullPath(root.RootPath, item.Path);
                HoldDirectory(Path.GetDirectoryName(link)!);
                using var linkHandle = Open(link, false);
                if (!PilotDirectoryLease.GetFileInformationByHandle(linkHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var expected = LocalItems.ParseObjectId(item.ObjectId);
                var (attributes, tag) = PilotPath.AttributeTag(linkHandle);
                if (info.VolumeSerialNumber != expected.Volume || (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != expected.FileId || !PilotPath.IsLink(attributes, tag))
                    throw new IOException("Link identity changed. Refresh the link list first.");
                var target = Show(root.RootPath, link);
                ValidateTarget(root.RootPath, link, target, allowOutside, confirmedTarget);
                var parent = Path.GetDirectoryName(target.Path);
                if (string.IsNullOrEmpty(parent)) throw new IOException("Target is a whole volume. Sync it separately." + Options);
                // The source itself is frozen below; its parent need not be locked (and may be a volume root).
                Scan(target.Path, "", 0);
                if (Show(root.RootPath, link) != target) throw new IOException("Link target changed during inspection. Show target again and confirm its current path.");
            }
            catch { Dispose(); throw; }
        }

        private void Scan(string path, string relative, int depth)
        {
            _ct.ThrowIfCancellationRequested();
            if (_entries.Count >= MaximumEntries || depth > 64) throw new IOException("Copy exceeds 4096 entries or 64 directory levels." + Options);
            // The target's own leaf name is replaced on publication, so only entries below it need a usable name.
            if (depth > 0)
            {
                try { PilotPath.NormalizeTopLevelName(Path.GetFileName(path)); }
                catch (ArgumentException)
                {
                    throw new IOException("Entry name not supported: " + path + ". Rename it in the target, or sync target separately." + Options);
                }
            }
            if (!_confirmed && _requiresConfirmation?.Invoke(path) == true)
                throw new IOException("Target contains an excluded, protected, or nested-pair path: " + path + ". Copying bypasses that exclusion. Show target, then explicitly confirm the exact target path with --name." + Options);
            PilotPath.RequireSeparatedFromProductState(path);
            using var inspection = Open(path, false);
            var (attributes, tag) = PilotPath.AttributeTag(inspection);
            if (PilotPath.IsLink(attributes, tag)) throw new IOException("Target contains another link (possibly a loop). Nested links are never followed." + Options);
            if (PilotPath.IsUnsafeHandle(inspection, attributes)) throw new IOException("Target contains online-only or unsupported content. Make it available offline first." + Options);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                HoldDirectory(path, source: true);
                _entries.Add((relative, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    Scan(child, relative.Length == 0 ? Path.GetFileName(child) : Path.Combine(relative, Path.GetFileName(child)), depth + 1);
                return;
            }
            var handle = Open(path, true);
            try
            {
                if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info) ||
                    (info.FileAttributes & FileAttributes.Directory) != 0 || PilotPath.IsUnsafeHandle(handle, info.FileAttributes) ||
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

        // The caller records this excluded location before creating it, so crashes retain a visible repair.
        internal static string StagingName() => SyncLocalTransfer.ArtifactPrefix + Guid.NewGuid().ToString("N") + ".partial";

        internal async Task CopyAsync(string destination, CancellationToken ct, string? stagingPath = null)
        {
            PilotPath.RequireSeparatedFromProductState(destination);
            var parent = HoldDirectory(Path.GetDirectoryName(destination)!);
            var name = PilotPath.NormalizeTopLevelName(Path.GetFileName(destination));
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Copy destination is occupied." + Options);
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < Bytes + MaximumBytes)
                throw new IOException("Free storage first; a link copy retains a 256 MiB reserve." + Options);
            stagingPath ??= Path.Combine(parent.Path, StagingName());
            if (!string.Equals(Path.GetDirectoryName(stagingPath), parent.Path, StringComparison.OrdinalIgnoreCase) ||
                !SyncLocalTransfer.IsTransferArtifactName(Path.GetFileName(stagingPath))) throw new IOException("Invalid copy staging location.");
            using var stage = PilotDirectoryLease.CreateChild(parent, Path.GetFileName(stagingPath), hidden: true);
            var payload = Path.Combine(stage.Path, "content");
            var destinations = new Dictionary<string, PilotDirectoryLease>(StringComparer.OrdinalIgnoreCase) { [""] = stage };
            var createdDirectories = new List<PilotDirectoryLease>();
            PilotDirectoryLease? topDirectory = null;
            SyncLocalFileAuthority? topFile = null;
            try
            {
                foreach (var (relative, content) in _entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = relative.Length == 0 ? payload : Path.Combine(payload, relative);
                    var parentKey = relative.Length == 0 ? "" : Path.GetDirectoryName(relative) ?? "";
                    var directory = destinations[parentKey];
                    directory.VerifyBinding();
                    if (content is null)
                    {
                        var created = PilotDirectoryLease.CreateChild(directory, Path.GetFileName(path), hidden: false);
                        if (relative.Length == 0) topDirectory = created;
                        else createdDirectories.Add(created);
                        destinations[relative] = created;
                    }
                    else
                    {
                        if (relative.Length == 0)
                        {
                            topFile = SyncLocalFileAuthority.CreateNewUnprotectedFile(path);
                            content.Position = 0;
                            await content.CopyToAsync(topFile.Stream, 81920, ct).ConfigureAwait(false);
                            topFile.Stream.Flush(flushToDisk: true);
                        }
                        else
                        {
                            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            content.Position = 0;
                            await content.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                            output.Flush(flushToDisk: true);
                        }
                    }
                    EntryCopiedForTest?.Invoke(path);
                }
                ct.ThrowIfCancellationRequested();
                stage.VerifyBinding();
                parent.VerifyBinding();
                // Release descendants only; the exact top object remains held from creation to publication.
                for (var i = createdDirectories.Count - 1; i >= 0; i--) createdDirectories[i].Dispose();
                createdDirectories.Clear();
                if (topDirectory is not null) topDirectory.RenameTo(parent, name);
                else topFile!.RenameTo(parent, name, replaceIfExists: false);
            }
            finally
            {
                for (var i = createdDirectories.Count - 1; i >= 0; i--) createdDirectories[i].Dispose();
                topDirectory?.Dispose();
                topFile?.Dispose();
            }
            // The empty staging directory remains excluded; no automatic deletion is authorised.
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
