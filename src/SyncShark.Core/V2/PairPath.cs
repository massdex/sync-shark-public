namespace Massdex.SyncShark.V2;

/// <summary>Pair-relative path helpers shared by the Planner, Executor and Run (paths use '/', no leading slash).</summary>
public static class PairPath
{
    /// <summary>True when the two paths name the same item (identity and occupancy compare case-insensitively).</summary>
    public static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="prefix"/>'s folder.</summary>
    public static bool IsUnder(string path, string prefix) =>
        prefix.Length > 0 && path.Length > prefix.Length &&
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path[prefix.Length] == '/';

    /// <summary>True when <paramref name="path"/> is the prefix itself or lies under it.</summary>
    public static bool IsSelfOrUnder(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) || IsUnder(path, prefix);

    /// <summary>
    /// The folders strictly above <paramref name="path"/>, deepest first: exactly the prefixes for which
    /// <see cref="IsUnder"/> holds, so a set of them answers "is anything under this folder" without a scan.
    /// </summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            yield return path[..slash];
    }

    /// <summary>Number of path segments (one plus the '/' count).</summary>
    public static int Depth(string path)
    {
        var depth = 1;
        foreach (var c in path)
            if (c == '/') depth++;
        return depth;
    }

    /// <summary>Rewrites a path that is self-or-under <paramref name="fromPrefix"/> to start at <paramref name="toPrefix"/>.</summary>
    public static string Rebase(string path, string fromPrefix, string toPrefix)
    {
        if (!IsSelfOrUnder(path, fromPrefix))
            throw new ArgumentException($"'{path}' is not self-or-under '{fromPrefix}'.", nameof(path));
        if (string.Equals(path, fromPrefix, StringComparison.OrdinalIgnoreCase))
            return toPrefix;
        var rest = path[(fromPrefix.Length + 1)..];
        return toPrefix.Length == 0 ? rest : toPrefix + "/" + rest;
    }
}
