namespace Massdex.SyncShark.Engine;

/// <summary>What is in sync for one pair: ignore rules, exclusions and nested pairs (section 2.3, D12, D25).
/// Configured exclusions are bilateral. Native nested roots and the PC bin are checked against their own mapped endpoint.</summary>
public sealed class PathScope
{
    private readonly DriveType localDriveType;
    private readonly PairConfig pair;
    private readonly IReadOnlyList<PairConfig> allPairs;
    private readonly string? defaultIgnoreRules;
    private readonly IReadOnlyList<IgnoreRule> ignoreRules;
    private readonly PrefixSet excluded = new();
    private readonly PrefixSet kept = new();
    private readonly PrefixSet localBoundaries = new();
    private readonly PrefixSet dropboxBoundaries = new();
    private readonly string[] localNestedRoots;
    private readonly string[] dropboxNestedRoots;
    private readonly string[] localNestedRootKeys;
    private readonly string[] dropboxNestedRootKeys;

    /// <summary>Builds the scope of one pair; other enabled pairs nested inside it are held out of sync.
    /// <paramref name="defaultIgnoreRules"/> uses the pair rule syntax; null applies none.</summary>
    public PathScope(PairConfig pair, IReadOnlyList<PairConfig> allPairs, DriveType localDriveType, string? defaultIgnoreRules = null)
    {
        this.pair = pair;
        this.localDriveType = localDriveType;
        if (localDriveType == DriveType.Removable)
        {
            var root = PathNames.NormalizeLocalPath(pair.LocalRoot);
            var bin = Path.Combine(Path.GetPathRoot(root)!, ResourceNames.RootBinFolder);
            if (PathNames.RelativeUnderLocalRoot(root, bin) is { } relative) localBoundaries.Add(PathNames.Key(relative));
            else if (PathNames.Same(root, bin) || PathNames.RelativeUnderLocalRoot(bin, root) is not null) localBoundaries.Add(string.Empty);
        }
        this.allPairs = allPairs;
        this.defaultIgnoreRules = defaultIgnoreRules;
        ignoreRules = IgnoreRule.Normalize(IgnoreRule.Parse(defaultIgnoreRules ?? "")
            .Concat(pair.IgnoreRules.SelectMany(IgnoreRule.Parse)).ToArray());
        foreach (var exclusion in pair.Exclusions)
        {
            var key = PathNames.KeyFor(exclusion.Side, exclusion.Path);
            excluded.Add(key);
            if (exclusion.Keep) kept.Add(key);
        }

        var local = new List<string>();
        var dropbox = new List<string>();
        foreach (var other in allPairs)
        {
            if (!other.Enabled || other.PairId == pair.PairId) continue;
            if (PathNames.RelativeUnderLocalRoot(pair.LocalRoot, other.LocalRoot) is { } localRoot) local.Add(localRoot);
            if (other.ConnectionId == pair.ConnectionId &&
                PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot),
                    PathNames.TrimDropboxRoot(other.DropboxRoot)) is { } dropboxRoot) dropbox.Add(dropboxRoot);
        }
        localNestedRoots = local.ToArray();
        dropboxNestedRoots = dropbox.ToArray();
        // Native boundaries apply to their actual mapped endpoint, never the other side’s spelling.
        localNestedRootKeys = CacheNestedRootKeys(localNestedRoots, Side.Local);
        dropboxNestedRootKeys = CacheNestedRootKeys(dropboxNestedRoots, Side.Dropbox);
    }

    /// <summary>Checks only the supplied actual endpoints. For a missing counterpart use LeavesScope to derive the mapped endpoint first.</summary>
    public bool Excludes(string? localPath, string? dropboxPath, bool isFolder) =>
        localPath is not null && ExcludesPath(localPath, isFolder, isDropbox: false) ||
        dropboxPath is not null && ExcludesPath(dropboxPath, isFolder, isDropbox: true);

    /// <summary>Tests both destinations of a move, deriving a missing side through the shared saved-parent mapping.</summary>
    public bool LeavesScope<T>(string? localPath, string? dropboxPath, bool isFolder, T state,
        Func<T, Side, string, SyncedEntry?> readSaved)
    {
        if (localPath is null && dropboxPath is not null)
            localPath = PathNames.MappedDestination(Side.Local, dropboxPath, state, readSaved);
        else if (dropboxPath is null && localPath is not null)
            dropboxPath = PathNames.MappedDestination(Side.Dropbox, localPath, state, readSaved);
        return Excludes(localPath, dropboxPath, isFolder);
    }

    /// <summary>Configured exclusions apply bilaterally; native boundaries are checked only for the specified side. Ignore rules are not consulted.</summary>
    public bool CoversKey(Side side,string key) => excluded.Covers(key) || (side==Side.Local ? localBoundaries : dropboxBoundaries).ResponsibilityOverlaps(key, false);

    /// <summary>Configured KEEP coverage only, never no-keep or ignore; used to follow a kept item's own move.</summary>
    public bool KeepCovers(Side side,string key) => kept.Covers(key);

    /// <summary>The configured exclusions this scope was built from, derived KEEP legs included.</summary>
    public IReadOnlyList<Exclusion> Exclusions => pair.Exclusions;

    /// <summary>True when the binding is absent or either actual endpoint lies under its applicable boundaries.</summary>
    public bool ExcludesBinding(SyncedEntry? binding) =>
        binding is null || CoversKey(Side.Local,binding.LocalKey) || CoversKey(Side.Dropbox,binding.DropboxKey);

    /// <summary>True only when each reported spelling is excluded. A saved binding supplies both actual endpoints; an unbound child uses the nearest mapped ancestor or the normal spelling fallback.</summary>
    public bool ExcludesChange<T>(Change notice, T state, Func<T, Side, string, SyncedEntry?> readSaved)
    {
        if (notice.Path is null) return false; // Unresolved historical binding is not proof of exclusion.
        var side = notice.Side;
        var other = PathNames.Other(side);
        // Each distinct path is evaluated once: a Dropbox notice observes its own path, and the key folds case.
        var old = PathNames.Same(notice.OldPath, notice.Path) ? null : notice.OldPath;
        var observed = notice.Observed?.Path is { } seen && !PathNames.Same(seen, notice.Path) &&
            !PathNames.Same(seen, old) ? seen : null;
        return Excludes(notice.Path) && (old is null || Excludes(old)) && (observed is null || Excludes(observed));

        bool Excludes(string path)
        {
            var key = PathNames.KeyFor(side, path);
            var saved=readSaved(state,side,key);
            if (saved is not null) return ExcludesBinding(saved);
            // Even without an exact binding, the destination of a new item is an actual write boundary.
            return CoversKey(side,key) || CoversKey(other, PathNames.KeyFor(other, PathNames.MappedDestination(other,path,state,readSaved)));
        }
    }

    /// <summary>True when a nested root of that side is at or under path.</summary>
    public bool HoldsNestedRoot(Side side, string path) => NestedRootTouch(side, path, savedKey: null);

    /// <summary>True when a nested root of that side is at or under path, or the path's own key or the caller's
    /// already computed join key for that side lies at or under one of them.</summary>
    public bool OverlapsNestedRoot(Side side, string path, string key) => NestedRootTouch(side, path, key);

    // One walk of the roots answers both questions; with no saved key only the direction holds asks for is tested.
    private bool NestedRootTouch(Side side, string path, string? savedKey)
    {
        var pathKey = PathNames.KeyFor(side, path);
        var roots = side == Side.Local ? localNestedRootKeys : dropboxNestedRootKeys;
        for (var i = 0; i < roots.Length; i++)
        {
            if (PathNames.IsSelfOrUnder(roots[i], pathKey)) return true;
            if (savedKey is not null && (PathNames.IsSelfOrUnderKey(savedKey, roots[i]) ||
                PathNames.IsSelfOrUnderKey(pathKey, roots[i]))) return true;
        }
        return false;
    }

    /// <summary>The nested roots of one side, relative to that side's pair root.</summary>
    public IReadOnlyList<string> NestedRoots(Side side) =>
        side == Side.Local ? localNestedRoots : dropboxNestedRoots;

    /// <summary>The same scope minus that one exclusion; used only by ExclusionRunner.</summary>
    public PathScope Without(Exclusion e) => new(
        pair with { Exclusions = pair.Exclusions.Where(exclusion => exclusion != e).ToArray() }, allPairs,
        localDriveType, defaultIgnoreRules);

    // A lost race builds an identical immutable value once; the scope's inputs never change after construction.
    private PathScope? configured;

    /// <summary>This pair's configured scope: the same pair with no exclusions, since an unapplied exclusion is not
    /// scope retirement (2.4). Derived once for this immutable scope's lifetime, and the scope itself when it holds
    /// no exclusions.</summary>
    public PathScope Configured
    {
        get
        {
            if (pair.Exclusions.Count == 0) return this;
            return configured ??= new(pair with { Exclusions = [] }, allPairs, localDriveType, defaultIgnoreRules);
        }
    }

    /// <summary>This pair's scope with no ignore rules (including the defaults) and that exclusion removed; used only
    /// by ExclusionRunner. The pair and pair list are this scope's, which match the run's while the instance lock is
    /// held.</summary>
    public PathScope ProtectionWithout(Exclusion exclusion) =>
        new(pair with
        {
            IgnoreRules = [],
            Exclusions = pair.Exclusions.Where(e => e != exclusion).ToArray()
        }, allPairs, localDriveType);

    private bool ExcludesPath(string path, bool isFolder, bool isDropbox) =>
        IgnoreRule.IsIgnored(path, isFolder, ignoreRules) ||
        CoversKey(isDropbox ? Side.Dropbox : Side.Local, PathNames.KeyFor(isDropbox ? Side.Dropbox : Side.Local,path));

    // The keys are computed once here, and the same instances form part of the exclusion set.
    private string[] CacheNestedRootKeys(string[] roots, Side side)
    {
        var keys = new string[roots.Length];
        for (var i = 0; i < roots.Length; i++) (side==Side.Local ? localBoundaries : dropboxBoundaries).Add(keys[i] = PathNames.KeyFor(side, roots[i]));
        return keys;
    }
}
