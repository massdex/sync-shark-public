namespace Massdex.SyncShark.V2;

/// <summary>Planner join: baselines and both sides' items into rows with evidence (planner.md section 2).</summary>
public static partial class Planner
{
    /// <summary>Joins baselines and both sides' items into rows with deletion and completeness evidence.</summary>
    public static IReadOnlyList<PlanRow> Join(IReadOnlyList<Baseline> b, IReadOnlyList<ItemState> a,
        IReadOnlyList<ItemState> bSide, IReadOnlyDictionary<string, DateTimeOffset?> deletedA,
        IReadOnlyDictionary<string, DateTimeOffset?> deletedB, Completeness c, bool firstSync,
        IReadOnlyList<ItemState>? contextA = null, IReadOnlyList<ItemState>? contextB = null,
        IReadOnlySet<string>? protectedAliases = null)
    {
        var coverage = new JoinCoverage(c);
        _ = firstSync; // frozen signature; Decide reads PlanOptions.FirstSync
        var claimed = new HashSet<ItemState>(ReferenceEqualityComparer.Instance);
        var rows = new List<PlanRow>();
        foreach (var (basis, curA, curB) in ClaimBaselines(b, a, bSide, claimed))
            rows.Add(new PlanRow(basis, curA, curB,
                EvidenceFor(curA, curB, basis.A.Path, basis.B.Path, deletedA, deletedB, coverage)));
        var collisions = MappingCollisions(a, bSide, b, rows);
        if (protectedAliases is not null) collisions.UnionWith(protectedAliases);
        if (contextA is { Count: > 0 } || contextB is { Count: > 0 })
            collisions.UnionWith(MappingCollisions(contextA ?? [], contextB ?? [], []));
        PairRemaining(rows, a, bSide, claimed, deletedA, deletedB, coverage, collisions);
        for (var i = 0; i < rows.Count; i++)
            if (MappingCollision(rows[i], collisions)) rows[i] = rows[i] with { NameMappingCollision = true };
        rows.Sort(CompareRows);
        return rows;
    }

    // Protection must claim from the unfiltered candidates, in exactly Join's order. In particular,
    // a moved protected identity can own an atomic replacement at its counterpart's old path.
    internal static IEnumerable<(Baseline Basis, ItemState? A, ItemState? B)> ClaimBaselines(
        IReadOnlyList<Baseline> b, IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> bSide,
        HashSet<ItemState>? claimed = null)
    {
        claimed ??= new HashSet<ItemState>(ReferenceEqualityComparer.Instance);
        var byIdA = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var byIdB = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var none = Array.Empty<ItemState>();                       // only baselines claim, by id or by path
        var pathA = ById(b.Count == 0 ? none : a, byIdA);
        var pathB = ById(b.Count == 0 ? none : bSide, byIdB);
        var baselines = b.OrderBy(x => x.ObjectKey, StringComparer.Ordinal).ToList();
        var idsA = new HashSet<string>(baselines.Select(x => x.A.ObjectId), StringComparer.Ordinal);
        var idsB = new HashSet<string>(baselines.Select(x => x.B.ObjectId), StringComparer.Ordinal);
        foreach (var basis in baselines)
            yield return (basis, Claim(byIdA, pathA, claimed, basis.A, idsA, Side.A),
                Claim(byIdB, pathB, claimed, basis.B, idsB, Side.B));
    }

    /// <summary>Pairs each side's unclaimed items by case-insensitive path, in one pass per side.</summary>
    private static void PairRemaining(List<PlanRow> rows, IReadOnlyList<ItemState> a, IReadOnlyList<ItemState> bSide,
        HashSet<ItemState> claimed, IReadOnlyDictionary<string, DateTimeOffset?> deletedA,
        IReadOnlyDictionary<string, DateTimeOffset?> deletedB, JoinCoverage c, HashSet<string> collisions)
    {
        var remainingA = a.Where(i => !claimed.Contains(i)).OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
        var remainingB = new PathChains(bSide.Where(i => !claimed.Contains(i)).OrderBy(i => i.Path, StringComparer.Ordinal).ToList(), true);
        var used = new HashSet<ItemState>(ReferenceEqualityComparer.Instance);
        foreach (var itemA in remainingA)
        {
            var path = DropboxNameMapping.MatchPath(itemA);
            var match = collisions.Contains(path) ? null :
                remainingB.At(path).FirstOrDefault(i => !used.Contains(i) &&
                    (i.Flags & ItemFlags.Ignored) == 0 && (itemA.Flags & ItemFlags.Ignored) == 0);   // ordinal-first unused
            if (match is not null && !PairPath.Same(itemA.Path, match.Path) && Content.SameContent(itemA, match) == false)
            {
                collisions.Add(path);
                match = null;
            }
            if (match is not null)
                used.Add(match);
            AddNoBase(rows, itemA, match, deletedA, deletedB, c);
        }
        foreach (var itemB in bSide.Where(i => !claimed.Contains(i) && !used.Contains(i)))
            AddNoBase(rows, null, itemB, deletedA, deletedB, c);
    }

    private static void AddNoBase(List<PlanRow> rows, ItemState? a, ItemState? b,
        IReadOnlyDictionary<string, DateTimeOffset?> deletedA, IReadOnlyDictionary<string, DateTimeOffset?> deletedB,
        JoinCoverage c)
    {
        var pathA = a?.Path ?? DropboxNameMapping.MatchPath(b!);
        var pathB = b?.Path ?? DropboxNameMapping.MatchPath(a!);
        rows.Add(new PlanRow(null, a, b, EvidenceFor(a, b, pathA, pathB, deletedA, deletedB, c)));
    }

    /// <summary>A row's evidence: a missing side's delete event and its time , and coverage.</summary>
    private static Evidence EvidenceFor(ItemState? a, ItemState? b, string pathA, string pathB,
        IReadOnlyDictionary<string, DateTimeOffset?> deletedA, IReadOnlyDictionary<string, DateTimeOffset?> deletedB,
        JoinCoverage c)
    {
        var atA = default(DateTimeOffset?);
        var atB = default(DateTimeOffset?);
        var isDeletedA = a is null && DeletedAt(deletedA, pathA, out atA);
        var isDeletedB = b is null && DeletedAt(deletedB, pathB, out atB);
        return new Evidence(isDeletedA, isDeletedB, Covered(c, Side.A, pathA), Covered(c, Side.B, pathB), atA, atB);
    }

    /// <summary>Fills the side's items by object id (the last wins) and returns them by case-insensitive path.</summary>
    private static PathChains ById(IReadOnlyList<ItemState> items, Dictionary<string, ItemState> byId)
    {
        foreach (var item in items)
            byId[item.ObjectId] = item;
        return new PathChains(items);
    }

    /// <summary>
    /// Items by case-insensitive path in list order, kept as one chain per path (a head per path and a next per item)
    /// instead of a list per path: a million-item side then costs one dictionary and one array.
    /// </summary>
    private sealed class PathChains
    {
        private readonly IReadOnlyList<ItemState> items;
        private readonly Dictionary<string, int> head;
        private readonly int[] next;

        public PathChains(IReadOnlyList<ItemState> items, bool mapped = false)
        {
            this.items = items;
            head = new Dictionary<string, int>(items.Count, StringComparer.OrdinalIgnoreCase);
            next = new int[items.Count];
            for (var j = items.Count - 1; j >= 0; j--)
            {
                var path = mapped ? DropboxNameMapping.MatchPath(items[j]) : items[j].Path;
                next[j] = head.TryGetValue(path, out var later) ? later : -1;
                head[path] = j;
            }
        }

        /// <summary>The items at the path ignoring case, in list order.</summary>
        public IEnumerable<ItemState> At(string path)
        {
            for (var j = head.TryGetValue(path, out var first) ? first : -1; j >= 0; j = next[j])
                yield return items[j];
        }
    }

    /// <summary>Claims the current item of a baseline: by object id, else the path fallback for atomic saves.</summary>
    private static ItemState? Claim(Dictionary<string, ItemState> byId, PathChains byPath,
        HashSet<ItemState> claimed, ItemState basis, HashSet<string> baselineIds, Side side)
    {
        if (byId.TryGetValue(basis.ObjectId, out var byIdItem) && !claimed.Contains(byIdItem))
        {
            claimed.Add(byIdItem);
            PlanProvenance.Current.Value?.Claim("baseline-identity-" + side, byIdItem);
            return byIdItem;
        }
        foreach (var item in byPath.At(basis.Path))
            if (!claimed.Contains(item) && !baselineIds.Contains(item.ObjectId))
            {
                claimed.Add(item);
                PlanProvenance.Current.Value?.Claim("replacement-path-" + side, item);
                return item;
            }
        return null;
    }

    /// <summary>True when the path or one of its ancestors carries a delete event; <paramref name="when"/> is its time.</summary>
    private static bool DeletedAt(IReadOnlyDictionary<string, DateTimeOffset?> deleted, string path,
        out DateTimeOffset? when)
    {
        when = null;
        if (deleted.Count == 0)
            return false;
        var current = path;
        while (true)
        {
            if (deleted.TryGetValue(current, out when))
                return true;
            var slash = current.LastIndexOf('/');
            if (slash < 0)
                return false;
            current = current[..slash];
        }
    }

    /// <summary>True when a complete inventory covers the path (no incomplete subtree above it; an empty one covers all).</summary>
    private sealed class JoinCoverage(Completeness completeness)
    {
        public readonly bool CompleteA = completeness.CompleteA;
        public readonly bool CompleteB = completeness.CompleteB;
        public readonly HashSet<string> IncompleteA = new(completeness.IncompleteSubtreesA, StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> IncompleteB = new(completeness.IncompleteSubtreesB, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Covered(JoinCoverage c, Side side, string path)
    {
        if (!(side == Side.A ? c.CompleteA : c.CompleteB)) return false;
        var incomplete = side == Side.A ? c.IncompleteA : c.IncompleteB;
        return PairPath.Observed(path, incomplete);
    }

    /// <summary>Orders rows by primary path (case-insensitive, then ordinal) with a total object-id tiebreak.</summary>
    private static List<PlanRow> OrderRows(IReadOnlyList<PlanRow> rows)
    {
        var sorted = true;                                    // Join's output already is: then the stable sort is a no-op
        for (var i = 1; i < rows.Count && sorted; i++)
            sorted = CompareRows(rows[i - 1], rows[i]) <= 0;
        return sorted ? rows.ToList() : rows
            .OrderBy(PrimaryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(PrimaryPath, StringComparer.Ordinal)
            .ThenBy(r => r.Base?.ObjectKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(r => r.A?.ObjectId ?? r.B?.ObjectId ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static int CompareRows(PlanRow x, PlanRow y)
    {
        var px = PrimaryPath(x);
        var py = PrimaryPath(y);
        var byName = string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
            return byName;
        var byOrdinal = string.Compare(px, py, StringComparison.Ordinal);
        if (byOrdinal != 0)
            return byOrdinal;
        var byKey = string.Compare(x.Base?.ObjectKey ?? string.Empty, y.Base?.ObjectKey ?? string.Empty, StringComparison.Ordinal);
        return byKey != 0 ? byKey : string.Compare(x.A?.ObjectId ?? x.B?.ObjectId ?? string.Empty, y.A?.ObjectId ?? y.B?.ObjectId ?? string.Empty, StringComparison.Ordinal);
    }
}
