namespace Massdex.SyncShark.V2;

/// <summary>A bounded, run-scoped snapshot of retry paths, not retry payloads or inventory items.</summary>
internal static class RetryProtection
{
    internal static Func<string, bool, bool> Create(IStateStore store, string pairId,
        IReadOnlySet<string> active) => Create(
            store.ReadSyncRetries(pairId).Select(retry => retry.Path), active,
            (path, descendants) => store.RetryProtected(pairId, path, active, descendants));

    internal static Func<string, bool, bool> Create(IEnumerable<string> paths, IReadOnlySet<string> active,
        Func<string, bool, bool> overflow, int pathLimit = PathProtectionIndex.DefaultPathLimit,
        int characterLimit = PathProtectionIndex.DefaultCharacterLimit)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var path in paths)
        {
            if (active.Contains(path) || !protectedPaths.Add(path)) continue;
            characters += path.Length;
            // Never turn a million-row retry backlog into a million managed strings. The indexed
            // store fallback retains existing semantics when the bounded snapshot does not fit.
            if (protectedPaths.Count > pathLimit || characters > characterLimit) return overflow;
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
