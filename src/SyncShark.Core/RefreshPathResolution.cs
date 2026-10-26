using System.ComponentModel;

namespace Massdex.SyncShark;

// Owns a refresh walk's requested-path spelling: the on-disk component names, the explicit children and
// the bounded per-parent listings shared with metadata lookup. The scanner only asks for those two.
internal sealed class RefreshPathResolution
{
    private readonly string _rootPath;
    private readonly Func<string, PilotDirectoryIdentity> _readIdentity;
    private readonly Action<int>? _retainedForTest;
    private readonly CancellationToken _cancellation;
    private readonly bool _boundedPreview;
    private readonly Dictionary<string, HashSet<string>> _wantedByParent = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _resolveOnDisk;
    private Dictionary<string, DirectoryEntryInfo[]>? _parentListings;

    internal RefreshPathResolution(string rootPath, IReadOnlyList<string>? refreshPaths, bool metadataOnly,
        bool boundedPreview, CancellationToken cancellation, Func<string, PilotDirectoryIdentity> readIdentity,
        Action<int>? retainedForTest)
    {
        _rootPath = rootPath;
        _readIdentity = readIdentity;
        _retainedForTest = retainedForTest;
        _cancellation = cancellation;
        _boundedPreview = boundedPreview;
        var relatives = refreshPaths?.Select(PilotPath.NormalizeRelative).ToArray();
        // One canonical key can only be reached twice when the request carries several paths, because a
        // single path is enumerated once and a folder's own children never repeat its key. A lone scoped
        // metadata read (content classification) therefore keeps its one parent listing, while a per-subtree
        // retry or journal refresh resolves every component. A bounded preview keeps its lazy single-parent
        // read too, so neither hot path gains a whole-tree ancestor walk.
        _resolveOnDisk = metadataOnly && !boundedPreview && relatives is { Length: > 1 };
        if (relatives is not null)
            foreach (var relative in relatives) Want(relative);
        ExplicitChildren = relatives?.Select(BuildChild).ToArray();
    }

    /// <summary>The absolute explicit children to scan, or null when this is not a refresh walk.</summary>
    internal string[]? ExplicitChildren { get; }

    /// <summary>A refresh path's containing-directory entry, cached per parent; vanished parents list nothing.</summary>
    internal DirectoryEntryInfo? ResolveFromParent(string exactPath,
        IReadOnlyDictionary<string, DirectoryEntryInfo>? listedMetadata)
    {
        if (listedMetadata is not null) return listedMetadata.GetValueOrDefault(exactPath);
        var parent = Path.GetDirectoryName(exactPath) ?? _rootPath;
        try
        {
            return ListParent(parent).FirstOrDefault(entry =>
            {
                _cancellation.ThrowIfCancellationRequested();
                return string.Equals(entry.Name, Path.GetFileName(exactPath), StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
    }

    // Resolution needs every component's parent retained; the ordinary refresh needs only each exact
    // requested path's own parent and leaf, exactly as the scanner did before this owner existed.
    private void Want(string relative)
    {
        if (!_resolveOnDisk)
        {
            var exact = Path.Combine(_rootPath, relative.Replace('/', Path.DirectorySeparatorChar));
            AddWanted(Path.GetDirectoryName(exact) ?? _rootPath, Path.GetFileName(exact));
            return;
        }
        var prefix = string.Empty;
        foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            AddWanted(prefix.Length == 0 ? _rootPath
                : Path.Combine(_rootPath, prefix.Replace('/', Path.DirectorySeparatorChar)), component);
            prefix = prefix.Length == 0 ? component : prefix + "/" + component;
        }
    }

    private void AddWanted(string parent, string name)
    {
        if (!_wantedByParent.TryGetValue(parent, out var wanted))
            _wantedByParent[parent] = wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        wanted.Add(name);
    }

    private string BuildChild(string requested)
    {
        var onDisk = _resolveOnDisk ? ResolveOnDiskSpelling(requested) : requested;
        if (!string.Equals(onDisk, requested, StringComparison.Ordinal))
            V2.RunTrace.Emit(PilotLocalScanner.RefreshCaseResolvedTrace + requested + " -> " + onDisk);
        return Path.Combine(_rootPath, onDisk.Replace('/', Path.DirectorySeparatorChar));
    }

    // The on-disk spelling of every requested component, or the requested path when a component is
    // absent or ambiguous. The cache is keyed case-insensitively, so a descendant resolves through
    // the same listing its already-resolved ancestor produced.
    private string ResolveOnDiskSpelling(string relative)
    {
        var resolved = new List<string>();
        foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = resolved.Count == 0 ? _rootPath
                : Path.Combine(_rootPath, string.Join('/', resolved).Replace('/', Path.DirectorySeparatorChar));
            var match = MatchComponent(parent, component);
            if (match is null) return relative;
            resolved.Add(match.Name);
        }
        return string.Join('/', resolved);
    }

    // Null when absent, or when two ordinal names in one case-sensitive directory match: never choose one.
    // A parent that cannot be listed also resolves nothing, so the ordinary per-item scan decision still
    // owns the failure instead of aborting the walk.
    private DirectoryEntryInfo? MatchComponent(string parent, string component)
    {
        DirectoryEntryInfo? match = null;
        try
        {
            foreach (var entry in ListParent(parent))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!string.Equals(entry.Name, component, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null) return null;
                match = entry;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return null; }
        return match;
    }

    // One owner for a refresh parent listing: the bounded set retained for a requested parent, or a
    // direct read for a bounded preview or an unrequested ancestor. A vanished parent lists nothing.
    private IEnumerable<DirectoryEntryInfo> ListParent(string parent)
    {
        if (!_boundedPreview && _wantedByParent.TryGetValue(parent, out var wanted))
        {
            _parentListings ??= new(StringComparer.OrdinalIgnoreCase);
            if (_parentListings.TryGetValue(parent, out var cached)) return cached;
            DirectoryEntryInfo[] listing;
            try
            {
                // Stream the directory once, retaining only the bounded refresh set, not every sibling.
                listing = WindowsDirectoryListing.List(parent).Where(entry =>
                {
                    _cancellation.ThrowIfCancellationRequested();
                    return wanted.Contains(entry.Name);
                }).ToArray();
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return []; }
            _parentListings[parent] = listing;
            _retainedForTest?.Invoke(listing.Length);
            return listing;
        }
        try
        {
            if (!string.Equals(parent, _rootPath, StringComparison.OrdinalIgnoreCase)) _readIdentity(parent);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return []; }
        return WindowsDirectoryListing.List(parent);
    }
}
