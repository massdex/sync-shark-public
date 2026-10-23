namespace Massdex.SyncShark.V2;

/// <summary>A bounded, run-scoped snapshot of retry paths, not retry payloads or inventory items.</summary>
internal static class RetryProtection
{
    internal const int PathLimit = RetryPathIndex.PathLimit;
    internal const int CharacterLimit = RetryPathIndex.CharacterLimit;

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
        int characterLimit = CharacterLimit, Action? onFallback = null) =>
        RetryPathIndex.Create(paths, active, overflow, pathLimit, characterLimit, onFallback);
}
