namespace Massdex.SyncShark.V2;

/// <summary>A bounded immutable protection snapshot. No database access occurs during a lookup.</summary>
public sealed class PathProtectionIndex
{
    public const int DefaultPathLimit = 50_000;
    public const int DefaultCharacterLimit = 4_000_000;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _ordered = new(StringComparer.OrdinalIgnoreCase);

    private PathProtectionIndex() { }

    /// <summary>Null means the caller must use bounded batch resolution, never individual SQL lookups.</summary>
    public static PathProtectionIndex? TryCreate(IEnumerable<string> paths, int pathLimit = DefaultPathLimit,
        int characterLimit = DefaultCharacterLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pathLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(characterLimit);
        var index = new PathProtectionIndex();
        long characters = 0;
        foreach (var path in paths)
        {
            if (!index._paths.Add(path)) continue;
            characters += path.Length;
            if (index._paths.Count > pathLimit || characters > characterLimit) return null;
            index._ordered.Add(path);
        }
        return index;
    }

    public int Count => _paths.Count;

    /// <summary>
    /// False: self or a protected ancestor. True: self or a protected descendant (not an ancestor).
    /// The empty path contains every protected item, but is not itself covered by a child.
    /// </summary>
    public bool Touches(string path, bool descendants)
    {
        if (_paths.Count == 0) return false;
        if (descendants)
        {
            if (path.Length == 0 || _paths.Contains(path)) return true;
            // Only stored protected paths are retained: no depth-multiplied ancestor set.
            var lower = path + "/";
            var candidate = _ordered.GetViewBetween(lower, path + "0").Min;
            return candidate is not null && candidate.StartsWith(lower, StringComparison.OrdinalIgnoreCase);
        }
        for (var current = path; current.Length > 0;)
        {
            if (_paths.Contains(current)) return true;
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
        return false;
    }
}
