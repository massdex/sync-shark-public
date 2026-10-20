using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Shared mappings, object-id helpers and the one exception translation for the local endpoint.</summary>
internal static class LocalItems
{
    private const string ObjectIdPrefix = "id:local:";
    private const int ShortIdLength = 32;

    /// <summary>Maps one metadata-scanned local entry to an item state; a missing NTFS identity fails loudly.</summary>
    public static ItemState FromScan(PilotLocalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var volume = entry.VolumeSerialNumber ?? throw new IOException("The local entry has no NTFS volume identity.");
        var fileId = entry.FileId ?? throw new IOException("The local entry has no NTFS file identity.");
        var kind = ToKind(entry.Kind);
        return new ItemState(entry.RelativePath, kind, SyncLocalEndpointStore.ObjectId(volume, fileId), null,
            entry.ContentHash, kind == ItemKind.Folder ? 0 : entry.Size, ToOffset(entry.LastWriteUtc), Flags(entry));
    }

    // Placeholder = an online-only cloud-files stub it exists, its content is only on the other
    // side. Unreadable = online-only attributes without a cloud-files placeholder: never opened, never a deletion.
    // Link = a junction/symlink/mount point, never followed and excluded on both sides (decision 59).
    private static ItemFlags Flags(PilotLocalEntry entry) =>
        entry.Link ? ItemFlags.Link
        : entry.ContentUnreadable ? ItemFlags.Unreadable
        : entry.CloudAvailability is null ? ItemFlags.None
        : ItemFlags.Placeholder;

    /// <summary>Maps one store entry and reads the current write time from disk for the mtime comparison.</summary>
    public static ItemState FromStore(PilotRemoteEntry entry, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kind = ToKind(entry.Kind);
        return new ItemState(entry.RelativePath, kind,
            entry.Id ?? throw new IOException("The store entry has no object identity."), null, entry.ContentHash,
            kind == ItemKind.Folder ? 0 : entry.Size, WriteTime(fullPath, entry.Kind), ItemFlags.None);
    }

    /// <summary>Builds the folder store entry the endpoint deletes or moves; a file must use the store's own entry.</summary>
    public static PilotRemoteEntry ToStoreEntry(ItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Kind != ItemKind.Folder)
            throw new InvalidOperationException("Only a folder store entry is synthesized; a file uses the store's entry.");
        return new PilotRemoteEntry(state.Path, PilotEntryKind.Directory, state.ObjectId, "directory", 0, null);
    }

    /// <summary>Builds the scan entry one item state describes, for hashing, reading and archive calls.</summary>
    public static PilotLocalEntry ToScanEntry(ItemState state, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (volume, fileId) = ParseObjectId(state.ObjectId);
        return new PilotLocalEntry(state.Path, state.Kind == ItemKind.Folder ? PilotEntryKind.Directory : PilotEntryKind.File,
            FullPath(rootPath, state.Path), state.Size, state.Mtime.UtcDateTime, state.Hash, volume, fileId);
    }

    /// <summary>Parses an <c>id:local:</c> object id into its volume serial and NTFS file id.</summary>
    public static (uint Volume, ulong FileId) ParseObjectId(string id)
    {
        if (id is null || !id.StartsWith(ObjectIdPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A local object id must start with '" + ObjectIdPrefix + "'.", nameof(id));
        var parts = id[ObjectIdPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[1].Length != 16 ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var volume) ||
            !ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileId))
            throw new ArgumentException(
                "A local object id must carry an 8-digit volume serial and a 16-digit file id.", nameof(id));
        return (volume, fileId);
    }

    /// <summary>True when any path segment is a Sync Shark product artifact that must never be inventoried.</summary>
    public static bool IsArtifactPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (SyncLocalRecovery.IsRecoveryFolderName(segment) ||
                segment.StartsWith(SyncControlNamespace.LocalPrefix, StringComparison.Ordinal) ||
                SyncLocalTransfer.IsTransferArtifactName(segment))
                return true;
        }
        return false;
    }

    /// <summary>True when the path is the empty whole-root prefix, a subtree, or a descendant of one.</summary>
    public static bool InsideAny(string path, IEnumerable<string> subtrees)
    {
        foreach (var subtree in subtrees)
            if (subtree.Length == 0 || PairPath.IsSelfOrUnder(path, subtree))
                return true;
        return false;
    }

    /// <summary>
    /// The one exclusion test for a '/'-separated root-relative path: a Sync Shark product artifact segment, or a
    /// path at or under one of the pair's product-owned folders. The inventory and the change watch share it.
    /// </summary>
    public static bool IsExcluded(string relative, IReadOnlySet<string> productOwnedRelativePaths)
    {
        if (IsArtifactPath(relative)) return true;
        foreach (var owned in productOwnedRelativePaths)
            if (owned.Length > 0 && (string.Equals(relative, owned, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(owned + "/", StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    /// <summary>The root-relative product-owned exclusion set one local root's scan and watch share.</summary>
    public static IReadOnlySet<string> ProductOwnedRelative(string rootPath, IReadOnlySet<string> productOwnedPaths) =>
        productOwnedPaths.Select(path => RelativeTo(rootPath, path)).Where(path => path is not null)
            .Select(path => path!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The '/'-separated root-relative form of a full local path, or null when it is the root itself or outside it.
    /// The shared bridge from a watcher's absolute path to the inventory's relative exclusion.
    /// </summary>
    public static string? RelativeTo(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        return relative is "" or "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            ? null
            : relative;
    }

    /// <summary>The full absolute path of a pair-relative item path under the root folder.</summary>
    public static string FullPath(string rootPath, string relative) =>
        Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The on-disk write time, in UTC ticks, of a file-or-folder the caller may compare to a state.</summary>
    public static long FileWriteTimeTicks(string rootPath, string relative) =>
        File.GetLastWriteTimeUtc(FullPath(rootPath, relative)).Ticks;

    /// <summary>The one exception translation for the local writer; an unmapped error is returned unchanged.</summary>
    public static Exception MapWriteError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is DropboxApiException { StatusCode: HttpStatusCode.Conflict })
            return new ExpectedTargetMismatchException(error.Message, error);
        if (error is IOException { InnerException: { } inner } && IsConflict(inner))
            return new ExpectedTargetMismatchException(error.Message, error);
        if (error is DropboxEffectUncertainException)
            return new UncertainEffectException(error.Message, error);
        return error;
    }

    // The reused directory-delete path surfaces Windows error 145 as a COMException HRESULT rather than a
    // Win32Exception; accept either carrier of the exact exists/exists/directory-not-empty native codes. The
    // COM carrier is matched by its exact HRESULT_FROM_WIN32 value, so an unrelated facility code is not a conflict.
    private static bool IsConflict(Exception inner) => inner switch
    {
        Win32Exception native => native.NativeErrorCode is 80 or 183 or 145,
        COMException com => com.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7) or
            unchecked((int)0x80070091),
        _ => false
    };

    // A scoped refresh lists each requested path's parent by name, so every ancestor folder must be the exact
    // object recorded on disk, inside the root and not a junction/reparse point, for as long as the listing runs.
    // Each checked ancestor stays held with delete sharing denied until the guard is disposed, so it cannot be
    // renamed, deleted or swapped for a junction in between. An ancestor proven absent makes its descendants
    // absent without any listing; an unsafe one makes its whole subtree "not known", deferred to the next full scan.
    public static RefreshGuard GuardRefreshPaths(string rootPath, string rootId, IStateStore known,
        IReadOnlyList<string> paths)
    {
        var guard = new RefreshGuard();
        var cache = new Dictionary<string, AncestorState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in paths)
            {
                var parts = path.Split('/');
                var state = AncestorState.Safe;
                var ancestor = string.Empty;
                for (var depth = 1; depth < parts.Length && state == AncestorState.Safe; depth++)
                {
                    ancestor = string.Join('/', parts.Take(depth));
                    state = Ancestor(rootPath, rootId, known, ancestor, cache, guard);
                }
                if (state == AncestorState.Safe) guard.Safe.Add(path);
                else if (state == AncestorState.Unsafe && !guard.Incomplete.Contains(ancestor)) guard.Incomplete.Add(ancestor);
            }
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private static AncestorState Ancestor(string rootPath, string rootId, IStateStore known, string ancestor,
        Dictionary<string, AncestorState> cache, RefreshGuard guard)
    {
        if (cache.TryGetValue(ancestor, out var cached)) return cached;
        AncestorState result;
        try
        {
            result = HoldAncestor(rootPath, rootId, known, ancestor, guard);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            result = AncestorState.Absent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or Win32Exception)
        {
            result = AncestorState.Unsafe;
        }
        cache[ancestor] = result;
        return result;
    }

    // Opens the ancestor once to learn its identity, then holds it with a listing handle (which, unlike an
    // attribute-only handle, takes part in sharing checks) pinned to that same identity.
    private static AncestorState HoldAncestor(string rootPath, string rootId, IStateStore known, string ancestor,
        RefreshGuard guard)
    {
        var full = FullPath(rootPath, ancestor);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            return AncestorState.Absent;
        // An online-only folder is never opened (its listing could need the absent provider): not known.
        if (PilotPath.IsOnlineOnlyAttributes(attributes) || PilotPath.IsUnsafePath(full))
            return AncestorState.Unsafe;
        PilotDirectoryIdentity identity;
        using (var probe = PilotDirectoryLease.Open(full)) identity = probe.Identity;
        var held = PilotDirectoryLease.OpenDirectoryReadGuard(full, identity);
        guard.Hold(held);
        var rows = known.Live(rootId, new[] { ancestor });
        var objectId = SyncLocalEndpointStore.ObjectId(identity.VolumeSerialNumber, identity.FileId);
        return PilotPath.IsPathInside(rootPath, held.Path) && (rows.Count == 0 || rows[0].ObjectId == objectId)
            ? AncestorState.Safe
            : AncestorState.Unsafe;
    }

    private enum AncestorState { Safe, Absent, Unsafe }

    /// <summary>The scoped-refresh paths whose ancestors are verified, holding those ancestors until disposed.</summary>
    internal sealed class RefreshGuard : IDisposable
    {
        private readonly List<PilotDirectoryLease> _leases = new();

        /// <summary>Paths whose every ancestor is held, inside the root and the recorded object.</summary>
        public List<string> Safe { get; } = new();

        /// <summary>Unsafe ancestors whose whole subtree is reported not known.</summary>
        public List<string> Incomplete { get; } = new();

        internal void Hold(PilotDirectoryLease lease) => _leases.Add(lease);

        /// <summary>Releases every held ancestor.</summary>
        public void Dispose()
        {
            foreach (var lease in _leases) lease.Dispose();
            _leases.Clear();
        }
    }

    /// <summary>A stable private control-namespace id for one pair root; deterministic per root.</summary>
    public static string NamespaceId(string rootId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ns:" + rootId)))
            .ToLowerInvariant()[..ShortIdLength];

    private static ItemKind ToKind(PilotEntryKind kind) => kind == PilotEntryKind.Directory ? ItemKind.Folder : ItemKind.File;

    private static DateTimeOffset WriteTime(string fullPath, PilotEntryKind kind) =>
        ToOffset(kind == PilotEntryKind.Directory ? Directory.GetLastWriteTimeUtc(fullPath) : File.GetLastWriteTimeUtc(fullPath));

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
