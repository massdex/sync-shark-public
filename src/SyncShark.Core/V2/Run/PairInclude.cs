using Massdex.SyncShark;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Decides whether one pair's side may touch a path: exclusions, nested pairs, product-owned folders, Sync Shark
/// artifacts. Ignore rules require item kinds and are applied after metadata inventory.
/// </summary>
/// <remarks>
/// <c>productOwned</c> are pair-relative folders the product writes inside a local root (data folder, spool,
/// Recovery). Like an exclusion they are protected on both sides, so a file at that path on the other side is never
/// planned into them, and a folder move or delete that contains one is rejected. A pair with a local side also never
/// touches a path with a Sync Shark artifact segment (control namespace, recovery or transfer artifact).
/// </remarks>
internal static class PairInclude
{
    /// <summary>Builds the inventory include predicate for one side: hides excluded and nested-pair paths.</summary>
    internal static Func<string, bool> For(PairDef pair, Side side, IReadOnlyList<PairDef> allPairs,
        IReadOnlyList<string> productOwned) =>
        Build(pair, side, allPairs, productOwned, rejectAncestors: false);

    /// <summary>
    /// Builds the predicate for one planned effect. Besides hiding excluded and nested-pair paths, it also
    /// rejects a path that CONTAINS such a path, so a folder move or delete can never touch another pair's
    /// area or an excluded subtree.
    /// </summary>
    internal static Func<string, bool> ForAction(PairDef pair, Side side, IReadOnlyList<PairDef> allPairs,
        IReadOnlyList<string> productOwned) =>
        Build(pair, side, allPairs, productOwned, rejectAncestors: true);

    private static Func<string, bool> Build(PairDef pair, Side side, IReadOnlyList<PairDef> allPairs,
        IReadOnlyList<string> productOwned, bool rejectAncestors)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var protectedPaths = pair.Excluded.Concat(productOwned).Select(Normalize).Where(path => path.Length > 0)
            .Concat(NestedRoots(root, pair.PairId, allPairs)).ToArray();
        var hasLocalSide = IsLocal(pair.A) || IsLocal(pair.B);
        return path =>
        {
            if (hasLocalSide && LocalItems.IsArtifactPath(path)) return false;
            foreach (var protectedPath in protectedPaths)
            {
                if (PairPath.IsSelfOrUnder(path, protectedPath)) return false;
                if (rejectAncestors && PairPath.IsUnder(protectedPath, path)) return false;
            }
            return true;
        };
    }

    /// <summary>Parses this pair's stored ignore-rule strings the way 'ignores set' does, then normalizes them.</summary>
    internal static IReadOnlyList<SyncIgnoreRule> IgnoreRules(PairDef pair) =>
        SyncIgnorePolicy.Normalize(pair.IgnoreRules.SelectMany(SyncIgnorePolicy.Parse).ToArray());

    // Every other pair's root that sits strictly inside this side's root, as a pair-relative '/'-path.
    private static IReadOnlyList<string> NestedRoots(RootDef root, string pairId, IReadOnlyList<PairDef> allPairs)
    {
        var roots = new List<string>();
        foreach (var other in allPairs)
        {
            if (string.Equals(other.PairId, pairId, StringComparison.Ordinal)) continue;
            foreach (var candidate in new[] { other.A, other.B })
                if (PairAdmission.NestedPath(root, candidate) is { } remainder) roots.Add(remainder);
        }
        return roots;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsLocal(RootDef root) =>
        string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase);
}
