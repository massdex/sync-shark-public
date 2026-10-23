namespace Massdex.SyncShark.V2;

/// <summary>A bounded, run-scoped snapshot of retry paths, not retry payloads or inventory items.</summary>
internal static class RetryProtection
{
    internal const int PathLimit = 150_000;
    internal const int CharacterLimit = 8_000_000;

    internal static Func<string, bool, bool> Create(IStateStore store, string pairId,
        IReadOnlySet<string> active, Action? onFallback = null) => Create(
            store.ReadSyncRetryPaths(pairId), active,
            CachedFallback(store, pairId, active), onFallback: onFallback);

    internal static Func<string, bool, bool> CachedFallback(IStateStore store, string pairId,
        IReadOnlySet<string> active)
    {
        var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var containers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        var gate = new object();
        bool Cached(Dictionary<string, bool> cache, string path, bool descendants)
        {
            lock (gate)
            {
                if (cache.TryGetValue(path, out var answer)) return answer;
                answer = store.RetryProtected(pairId, path, active, descendants);
                if (folders.Count + containers.Count >= 4096 || characters + path.Length > 262_144)
                { folders.Clear(); containers.Clear(); characters = 0; }
                if (path.Length <= 262_144) { cache[path] = answer; characters += path.Length; }
                return answer;
            }
        }
        return (path, descendants) =>
        {
            if (descendants) return Cached(containers, path, true);
            if (!active.Contains(path) && store.SyncRetryExists(pairId, path)) return true;
            var cut = path.LastIndexOf('/');
            return cut > 0 && Cached(folders, path[..cut], false);
        };
    }

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
