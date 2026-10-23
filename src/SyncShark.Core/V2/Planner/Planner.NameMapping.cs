namespace Massdex.SyncShark.V2;

public static partial class Planner
{
    private static readonly NoticeReason MappingAmbiguous = new("name-mapping-collision",
        "More than one item can use this Windows spelling. Both spellings are kept unchanged. " +
        "Options: rename one item to a unique name and sync again, or exclude it.");

    // Only mapped names need extra indexing; ordinary million-item inventories add no second full index.
    private static HashSet<string> MappingCollisions(IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> b,
        IReadOnlyList<Baseline> baselines, IReadOnlyList<PlanRow>? joined = null)
    {
        var counts = new Dictionary<string, (int A, int B, bool IgnoredA, bool IgnoredB)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in a.Concat(b))
            if (DropboxNameMapping.LocalPath(item) is { } path) counts.TryAdd(path, default);
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in a)
        {
            var path = DropboxNameMapping.MatchPath(item);
            if (counts.TryGetValue(path, out var n))
                counts[path] = Active(item) ? (n.A + 1, n.B, n.IgnoredA, n.IgnoredB) : (n.A, n.B, true, n.IgnoredB);
        }
        foreach (var item in b)
        {
            var path = DropboxNameMapping.MatchPath(item);
            if (counts.TryGetValue(path, out var n))
                counts[path] = Active(item) ? (n.A, n.B + 1, n.IgnoredA, n.IgnoredB) : (n.A, n.B, n.IgnoredA, true);
        }
        // Ignored entities are not matching candidates, but still occupy their spelling. If the
        // only counterpart is ignored, hold the active entity instead of redirecting its create
        // to a labelled copy. An active literal pair remains eligible when an unlinked alias is ignored.
        foreach (var (path, n) in counts)
            if (n.A > 1 || n.B > 1 || n.A > 0 && n.B == 0 && n.IgnoredB ||
                n.B > 0 && n.A == 0 && n.IgnoredA) collisions.Add(path);
        // A previously linked underscore entity is not available to a different cloud entity.
        var mappedIdsA = MappedIds(a);
        var mappedIdsB = MappedIds(b);
        foreach (var basis in baselines)
        {
            var pathA = DropboxNameMapping.MatchPath(basis.A);
            var pathB = DropboxNameMapping.MatchPath(basis.B);
            if (!DropboxNameMapping.SamePlace(basis.A, basis.B) ||
                mappedIdsB.TryGetValue(pathA, out var idB) && idB != basis.B.ObjectId ||
                mappedIdsA.TryGetValue(pathB, out var idA) && idA != basis.A.ObjectId)
            {
                if (counts.ContainsKey(pathA)) collisions.Add(pathA);
                if (counts.ContainsKey(pathB)) collisions.Add(pathB);
            }
        }
        // A moved, already-bound cloud identity cannot adopt an unrelated destination occupant.
        CheckDestinations(a, b, true);
        CheckDestinations(b, a, false);
        return collisions;

        void CheckDestinations(IReadOnlyList<ItemState> cloud, IReadOnlyList<ItemState> other, bool cloudA)
        {
            var mappedIds = cloud.Where(i => Active(i) && DropboxNameMapping.LocalPath(i) is not null)
                .Select(i => i.ObjectId).ToHashSet(StringComparer.Ordinal);
            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var basis in baselines)
                if (mappedIds.Contains(cloudA ? basis.A.ObjectId : basis.B.ObjectId))
                    bound.TryAdd(cloudA ? basis.A.ObjectId : basis.B.ObjectId, cloudA ? basis.B.ObjectId : basis.A.ObjectId);
            // Claim already handles save-by-temp-and-rename and excludes identities owned by other baselines.
            foreach (var row in joined ?? [])
                if (row.Base is { } basis && (cloudA ? row.B : row.A) is { } claimed &&
                    mappedIds.Contains(cloudA ? basis.A.ObjectId : basis.B.ObjectId))
                    bound[cloudA ? basis.A.ObjectId : basis.B.ObjectId] = claimed.ObjectId;
            var mapped = cloud.Where(i => Active(i) && DropboxNameMapping.LocalPath(i) is not null && bound.ContainsKey(i.ObjectId))
                .GroupBy(DropboxNameMapping.MatchPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in other.Where(Active))
                if (mapped.TryGetValue(item.Path, out var candidates) && candidates.Any(c => bound[c.ObjectId] != item.ObjectId))
                    collisions.Add(item.Path);
        }
    }

    private static bool Active(ItemState item) => (item.Flags & ItemFlags.Ignored) == 0;

    private static Dictionary<string, string> MappedIds(IReadOnlyList<ItemState> items)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(Active))
            if (DropboxNameMapping.LocalPath(item) is { } path) result.TryAdd(path, item.ObjectId);
        return result;
    }

    private static bool MappingCollision(PlanRow row, HashSet<string> collisions)
    {
        if (row.Base is null && row.A is not null && row.B is not null &&
            !PairPath.Same(row.A.Path, row.B.Path) && Content.SameContent(row.A, row.B) != true) return true;
        return Hit(row.A) || Hit(row.B) || Hit(row.Base?.A) || Hit(row.Base?.B);
        bool Hit(ItemState? item) => collisions.Count != 0 && item is not null &&
            (collisions.Contains(DropboxNameMapping.MatchPath(item)) ||
             PairPath.Ancestors(DropboxNameMapping.MatchPath(item)).Any(collisions.Contains));
    }

    private sealed class NameMappings
    {
        private readonly Dictionary<string, string> cloudA = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> cloudB = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> protectedA = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> protectedB = new(StringComparer.OrdinalIgnoreCase);
        private bool mappedA, mappedB;

        public NameMappings(IReadOnlyList<PlanRow> rows, PlanOptions options)
        {
            foreach (var item in options.MappingContextA.Where(Active)) Add(item, Side.A);
            foreach (var item in options.MappingContextB.Where(Active)) Add(item, Side.B);
            foreach (var row in rows)
            {
                Add(row.A ?? row.Base?.A, Side.A);
                Add(row.B ?? row.Base?.B, Side.B);
                if (!row.NameMappingCollision || row.A is { } a && !Active(a) || row.B is { } b && !Active(b)) continue;
                Protect(row.A ?? row.Base?.A, Side.A);
                Protect(row.B ?? row.Base?.B, Side.B);
            }
        }

        private void Add(ItemState? item, Side side)
        {
            if (item is null || !Active(item) || DropboxNameMapping.LocalPath(item) is not { } local) return;
            if (side == Side.A) mappedA = true; else mappedB = true;
            (side == Side.A ? cloudA : cloudB).TryAdd(local, item!.Path);
        }

        private void Protect(ItemState? item, Side side)
        {
            if (item is null) return;
            var paths = side == Side.A ? protectedA : protectedB;
            paths.Add(item.Path);
            foreach (var ancestor in PairPath.Ancestors(item.Path)) paths.Add(ancestor);
        }

        public bool Protects(SyncAction action) => action.Kind == ActionKind.Delete &&
            (action.Target == Side.A ? protectedA : protectedB).Contains(action.Path);

        public string TargetPath(Side target, string path)
        {
            var own = target == Side.A ? cloudA : cloudB;
            var otherMapped = target == Side.A ? mappedB : mappedA;
            if (own.Count == 0) return otherMapped ? DropboxNameMapping.Map(path) : path;
            if (own.TryGetValue(path, out var original)) return original;
            foreach (var ancestor in PairPath.Ancestors(path))
                if (own.TryGetValue(ancestor, out original)) return original + path[ancestor.Length..];
            return path;
        }
    }
}
