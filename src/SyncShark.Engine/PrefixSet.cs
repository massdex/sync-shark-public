namespace Massdex.SyncShark.Engine;

/// <summary>A set of keys that also covers their descendants (segment boundary, section 8.3).</summary>
public sealed class PrefixSet
{
    private readonly HashSet<string> _keys = new(PathNames.Comparer);
    private volatile HashSet<string>? _ancestors;

    /// <summary>Marks key and its descendants as covered.</summary>
    public void Add(string key)
    {
        _keys.Add(key);
        _ancestors = null;
    }

    /// <summary>True when key covers or is covered by a stored key at a path-segment boundary.</summary>
    public bool Overlaps(string key)
    {
        if (_keys.Count == 0) return false;
        if (Covers(key)) return true;
        // Add finishes before row readers start, so racing builds produce identical sets.
        var ancestors = _ancestors ??= new HashSet<string>(_keys.SelectMany(PathNames.Ancestors), PathNames.Comparer);
        return ancestors.Contains(key);
    }

    /// <summary>Responsibility covers descendants, and descendants block a considered folder; root covers all.</summary>
    internal static bool ResponsibilityOverlaps(string key, string otherKey, bool isFolder) =>
        otherKey.Length == 0 || PathNames.IsSelfOrUnderKey(key, otherKey) ||
        isFolder && (key.Length == 0 || PathNames.IsUnderKey(otherKey, key));

    /// <summary>The indexed form of the single-key responsibility predicate.</summary>
    internal bool ResponsibilityOverlaps(string key, bool isFolder) =>
        _keys.Contains(string.Empty) || isFolder && key.Length == 0 && _keys.Count != 0 ||
        (isFolder ? Overlaps(key) : Covers(key));

    public IEnumerable<string> Keys => _keys.Count == 0 ? Array.Empty<string>() : _keys.Select(static key => key);

    public void Clear()
    {
        _keys.Clear();
        _ancestors = null;
    }

    /// <summary>True when key equals a stored key or sits under one at a path-segment boundary.</summary>
    public bool Covers(string key)
    {
        // Most rows meet empty sets; skip the per-ancestor substrings then.
        if (_keys.Count == 0) return false;
        if (_keys.Contains(key)) return true;
        for (var boundary = PathNames.NextSeparator(key, 0); boundary > 0;
             boundary = PathNames.NextSeparator(key, boundary + 1))
        {
            if (_keys.Contains(key[..boundary])) return true;
        }
        return false;
    }
}
