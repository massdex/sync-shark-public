namespace Massdex.SyncShark.V2;

/// <summary>
/// Which pair layouts may coexist, decided once for pair creation, enabling and before every run (the v2 home of the
/// admission rule v1 kept in SyncPairPartitionPolicy). Paths are canonical per provider and compare
/// case-insensitively, only on the same store (provider and account). Observed root identities compare across
/// accounts, because a shared Dropbox folder keeps one id in every account and a local folder keeps one volume
/// and file id behind every path alias; a root without an observed identity is compared by path only (a new pair
/// before observation; the create path refuses a root it cannot observe). An equal root is refused. Nesting in one direction is admitted, because
/// <see cref="PairInclude"/> always keeps the enclosing pair out of the nested root (v1 admitted nesting only when
/// the enclosing pair excluded the nested subtree). Nesting in opposite directions on different sides is refused,
/// as v1 refused that mixed direction as unproven, and so is a relation that cannot be decided.
/// </summary>
public static class PairAdmission
{
    /// <summary>
    /// The refusal for <paramref name="candidate"/> against every pair in <paramref name="others"/>, comparing paths
    /// and, where both roots carry one, observed root identities. Null admits.
    /// </summary>
    public static NoticeReason? Decide(PairDef candidate, IEnumerable<PairDef> others)
    {
        foreach (var other in others)
            if (Decide(candidate, other) is { } refusal) return refusal;
        return null;
    }

    /// <summary>
    /// <see cref="Decide"/> for a saved pair against the saved pairs, skipping its own record: the check before
    /// enabling a pair and before every run.
    /// </summary>
    public static NoticeReason? DecideSaved(PairDef pair, IEnumerable<PairDef> saved) =>
        Decide(pair, saved.Where(other => !string.Equals(other.PairId, pair.PairId, StringComparison.Ordinal)));

    /// <summary>
    /// The canonical root path for one provider. Local: a fully qualified path through
    /// <see cref="Path.GetFullPath(string)"/>, so '.', '..' and '/' are resolved, without a trailing separator
    /// except at a drive root. Cloud: '/'-separated with one leading '/', repeated separators collapsed and no
    /// trailing '/'; '.' and '..' segments are refused, because a cloud path never resolves them.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, relative, malformed or has a dot segment.</exception>
    public static string Canonical(string provider, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("The root path is empty.", nameof(rootPath));
        if (IsLocal(provider))
        {
            if (!Path.IsPathFullyQualified(rootPath))
                throw new ArgumentException("A local root must be a fully qualified path.", nameof(rootPath));
            var full = Path.GetFullPath(rootPath);
            return string.Equals(Path.GetPathRoot(full), full, StringComparison.Ordinal) ? full : full.TrimEnd('\\');
        }
        var segments = rootPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("A cloud root may not contain '.' or '..' segments.", nameof(rootPath));
        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// The '/'-path of <paramref name="child"/> relative to <paramref name="parent"/> when the child root lies
    /// strictly inside the parent root on the same store; otherwise (or when either path is not usable) null.
    /// </summary>
    public static string? NestedPath(RootDef parent, RootDef child)
    {
        if (!SameStore(parent, child) || Key(parent) is not { } outer || Key(child) is not { } inner) return null;
        if (!Encloses(outer, inner)) return null;
        return outer.Length == 0 ? inner : inner[(outer.Length + 1)..];
    }

    private static NoticeReason? Decide(PairDef candidate, PairDef other)
    {
        var candidateEncloses = false;
        var otherEncloses = false;
        foreach (var mine in new[] { candidate.A, candidate.B })
        foreach (var theirs in new[] { other.A, other.B })
        {
            if (!string.Equals(mine.Provider, theirs.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            // A root without an observed identity (a pair not observed yet, or a legacy record) is compared by its
            // path only; the identity comparison needs both ids.
            if (!string.IsNullOrEmpty(mine.RootObjectId) && !string.IsNullOrEmpty(theirs.RootObjectId) &&
                string.Equals(mine.RootObjectId, theirs.RootObjectId, StringComparison.Ordinal))
                return PairAdmissionReasons.SameRoot;
            if (!string.Equals(mine.AccountId, theirs.AccountId, StringComparison.Ordinal)) continue;
            if (Key(mine) is not { } mineKey || Key(theirs) is not { } theirsKey) return PairAdmissionReasons.Unresolved;
            if (PairPath.Same(mineKey, theirsKey)) return PairAdmissionReasons.SameRoot;
            candidateEncloses |= Encloses(mineKey, theirsKey);
            otherEncloses |= Encloses(theirsKey, mineKey);
        }
        return candidateEncloses && otherEncloses ? PairAdmissionReasons.CrossedNesting : null;
    }

    // A provider root (Dropbox "/") has the empty key, which encloses every other path on that store.
    private static bool Encloses(string outer, string inner) =>
        outer.Length == 0 ? inner.Length > 0 : PairPath.IsUnder(inner, outer);

    private static bool SameStore(RootDef x, RootDef y) =>
        string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.AccountId, y.AccountId, StringComparison.Ordinal);

    private static bool IsLocal(string provider) =>
        string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase);

    // The canonical root in PairPath form ('/' separators, no leading or trailing '/'), or null when unusable.
    private static string? Key(RootDef root)
    {
        try
        {
            return Canonical(root.Provider, root.RootPath).Replace('\\', '/').Trim('/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
