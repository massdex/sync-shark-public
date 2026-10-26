using System.ComponentModel;
using System.Globalization;

namespace Massdex.SyncShark;

// Owns a refresh walk's requested-path spelling: the on-disk component names, the explicit children and
// the bounded per-parent listings shared with metadata lookup. The scanner only asks for those three.
internal sealed class RefreshPathResolution
{
    private readonly string _rootPath;
    private readonly Func<string, PilotDirectoryIdentity> _readIdentity;
    private readonly Action<int>? _retainedForTest;
    private readonly CancellationToken _cancellation;
    private readonly bool _boundedPreview;
    // Parent paths are keyed ORDINALLY. Two names that differ only by case are different directories in a
    // case-sensitive tree, so one parent's listing must never answer for its case variant. The wanted names
    // inside one parent stay case-insensitive so every ordinal match is retained and ambiguity stays visible.
    private readonly Dictionary<string, HashSet<string>> _wantedByParent = new(StringComparer.Ordinal);
    private readonly List<string> _ambiguous = new();
    private readonly HashSet<string> _ambiguousSeen = new(StringComparer.Ordinal);
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
        if (relatives is null) return;
        foreach (var relative in relatives) Want(relative);
        var children = new List<string>(relatives.Length);
        foreach (var relative in relatives)
        {
            var onDisk = _resolveOnDisk ? ResolveOnDiskSpelling(relative) : relative;
            if (onDisk is null)
            {
                // Two ordinal names answer the request: never pick one. The path stays incomplete instead.
                if (_ambiguousSeen.Add(relative)) _ambiguous.Add(relative);
                continue;
            }
            if (!string.Equals(onDisk, relative, StringComparison.Ordinal))
                V2.RunTrace.Emit(string.Format(CultureInfo.InvariantCulture,
                    V2.MessageCatalog.Default.Template("refresh-case-resolved"), relative, onDisk));
            children.Add(Path.Combine(_rootPath, onDisk.Replace('/', Path.DirectorySeparatorChar)));
        }
        ExplicitChildren = children.ToArray();
    }

    /// <summary>The absolute explicit children to scan, or null when this is not a refresh walk.</summary>
    internal string[]? ExplicitChildren { get; }

    /// <summary>Requested paths whose components match more than one ordinal name; the walk must not read them.</summary>
    internal IReadOnlyList<string> AmbiguousPaths => _ambiguous;

    /// <summary>
    /// A refresh path's containing-directory entry, cached per parent; vanished parents list nothing.
    /// More than one ordinal match sets <paramref name="ambiguous"/>: the caller reports a collision rather
    /// than borrowing one entry's identity for the other name.
    /// </summary>
    internal DirectoryEntryInfo? ResolveFromParent(string exactPath,
        IReadOnlyDictionary<string, DirectoryEntryInfo>? listedMetadata, out bool ambiguous)
    {
        ambiguous = false;
        if (listedMetadata is not null) return listedMetadata.GetValueOrDefault(exactPath);
        var parent = Path.GetDirectoryName(exactPath) ?? _rootPath;
        var leaf = Path.GetFileName(exactPath);
        try
        {
            DirectoryEntryInfo? match = null;
            foreach (var entry in ListParent(parent))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!string.Equals(entry.Name, leaf, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null)
                {
                    ambiguous = true;
                    return null;
                }
                match = entry;
            }
            return match;
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

    // The on-disk spelling of every requested component, or the requested path when a component is absent.
    // Null when one component matches more than one ordinal name: no spelling may be chosen for it.
    private string? ResolveOnDiskSpelling(string relative)
    {
        var resolved = new List<string>();
        var requested = new List<string>();
        foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = ParentOf(resolved);
            var requestedParent = ParentOf(requested);
            var match = MatchComponent(parent, component, out var ambiguous);
            if (ambiguous) return null;
            if (match is null) return relative;
            // The bounded wanted set cached for the requested spelling answers the real one, level by level,
            // so a descendant resolves through the same listing its already-resolved ancestor produced.
            AdoptWanted(Path.Combine(requestedParent, component), Path.Combine(parent, match.Name));
            resolved.Add(match.Name);
            requested.Add(component);
        }
        return string.Join('/', resolved);
    }

    private string ParentOf(IReadOnlyList<string> components) => components.Count == 0 ? _rootPath
        : Path.Combine(_rootPath, string.Join('/', components).Replace('/', Path.DirectorySeparatorChar));

    // A single case-insensitive match proved both spellings name one object, so the wanted names follow it.
    private void AdoptWanted(string requestedChild, string actualChild)
    {
        if (string.Equals(requestedChild, actualChild, StringComparison.Ordinal) ||
            !_wantedByParent.TryGetValue(requestedChild, out var wanted)) return;
        if (!_wantedByParent.TryGetValue(actualChild, out var actual))
            _wantedByParent[actualChild] = actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = actual.Count;
        actual.UnionWith(wanted);
        // A listing retained under the narrower wanted set must not answer the widened one: a second
        // requested spelling would otherwise miss its own child and the walk would admit it absent.
        if (actual.Count != retained) _parentListings?.Remove(actualChild);
    }

    // Null when absent, or when two ordinal names in one case-sensitive directory match: never choose one.
    // A parent that cannot be listed also resolves nothing, so the ordinary per-item scan decision still
    // owns the failure instead of aborting the walk.
    private DirectoryEntryInfo? MatchComponent(string parent, string component, out bool ambiguous)
    {
        ambiguous = false;
        DirectoryEntryInfo? match = null;
        try
        {
            foreach (var entry in ListParent(parent))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!string.Equals(entry.Name, component, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null)
                {
                    ambiguous = true;
                    return null;
                }
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
            _parentListings ??= new(StringComparer.Ordinal);
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
