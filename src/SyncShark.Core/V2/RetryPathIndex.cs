namespace Massdex.SyncShark.V2;

internal static class RetryPathIndex
{
    internal const int PathLimit = 150_000;
    internal const int CharacterLimit = 8_000_000;

    internal static Func<string, bool, bool> Create(IEnumerable<string> paths, IReadOnlySet<string> active,
        Func<string, bool, bool> overflow, int pathLimit = PathLimit,
        int characterLimit = CharacterLimit, Action? onFallback = null)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var path in paths)
        {
            if (active.Contains(path) || !protectedPaths.Add(path)) continue;
            characters += path.Length;
            // Never turn a million-row retry backlog into a million managed strings. The indexed
            // store fallback retains existing semantics when the bounded snapshot does not fit.
            if (protectedPaths.Count > pathLimit || characters > characterLimit)
            {
                onFallback?.Invoke();
                return overflow;
            }
        }
        var ordered = protectedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return (path, descendants) =>
        {
            if (protectedPaths.Count == 0) return false;
            if (descendants)
            {
                if (path.Length == 0) return true;
                var prefix = path + "/";
                var index = Array.BinarySearch(ordered, prefix, StringComparer.OrdinalIgnoreCase);
                if (index < 0) index = ~index;
                return index < ordered.Length && ordered[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            if (protectedPaths.Contains(path)) return true;
            foreach (var ancestor in PairPath.Ancestors(path))
                if (protectedPaths.Contains(ancestor)) return true;
            return false;
        };
    }
}
